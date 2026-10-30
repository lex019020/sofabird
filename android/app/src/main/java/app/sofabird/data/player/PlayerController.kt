package app.sofabird.data.player

import android.util.Log
import app.sofabird.data.companion.BluetoothStatus
import app.sofabird.data.companion.CompanionBackend
import app.sofabird.data.companion.CompanionCredentials
import app.sofabird.data.companion.CompanionEndpoint
import app.sofabird.data.companion.CompanionTransports
import app.sofabird.data.net.ArtAuth
import app.sofabird.data.net.PinnedServers
import app.sofabird.data.secrets.SecretStore
import app.sofabird.data.servers.ServerRepository
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.domain.model.CompanionSighting
import app.sofabird.domain.model.MoonlightChoice
import app.sofabird.domain.model.SavedServer
import app.sofabird.domain.model.mergeSighting
import app.sofabird.protocol.backend.InputBatch
import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.backend.PlayerBackend
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.backend.onTv
import app.sofabird.protocol.backend.resultOf
import app.sofabird.protocol.backend.routeToYouTube
import app.sofabird.protocol.companion.AudioDevice
import app.sofabird.protocol.companion.DisplayList
import app.sofabird.protocol.companion.lanRoute
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.HistoryChange
import app.sofabird.protocol.model.HistoryFilter
import app.sofabird.protocol.model.HistoryPage
import app.sofabird.protocol.model.Library
import app.sofabird.protocol.model.MoonlightHost
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlaylistItem
import app.sofabird.protocol.model.ShowDetails
import app.sofabird.protocol.model.VlcVolume
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** One-shot things the UI shows as snackbars/overlays. */
sealed class PlayerEvent {
    data class CommandFailed(val command: PlayerCommand, val error: Throwable) : PlayerEvent()
    data class SystemCommandFailed(val command: SystemCommand, val error: Throwable) : PlayerEvent()
    data class VolumeChanged(val percent: Int) : PlayerEvent()
    data class Paired(val server: SavedServer) : PlayerEvent()

    /** A data request that changes something on the PC (watch history edits) failed. */
    data class RequestFailed(val error: Throwable) : PlayerEvent()
}

/**
 * The single owner of the active [PlayerBackend]. Everything in the app (screens, volume keys,
 * the foreground service) talks to the player through this class.
 */
class PlayerController(
    private val servers: ServerRepository,
    private val secrets: SecretStore,
    private val artAuth: ArtAuth,
    private val pinned: PinnedServers,
    private val appVisibility: AppVisibility,
    private val identity: DeviceIdentity,
    private val transports: CompanionTransports,
    private val settings: SettingsRepository,
    private val scope: CoroutineScope,
) {
    private val _activeServer = MutableStateFlow<SavedServer?>(null)
    val activeServer: StateFlow<SavedServer?> = _activeServer.asStateFlow()

    private val _backend = MutableStateFlow<PlayerBackend?>(null)
    val backend: StateFlow<PlayerBackend?> = _backend.asStateFlow()

    val connection: StateFlow<ConnectionState> = _backend
        .flatMapLatest { it?.connection ?: flowOf(ConnectionState.Disconnected) }
        .stateIn(scope, SharingStarted.Eagerly, ConnectionState.Disconnected)

    val state: StateFlow<PlayerState> = _backend
        .flatMapLatest { it?.state ?: flowOf(PlayerState.EMPTY) }
        .stateIn(scope, SharingStarted.Eagerly, PlayerState.EMPTY)

    /**
     * What the TV shows as a player: YouTube while it is open in the TV browser, otherwise VLC ([PlayerState.onTv]).
     * The media notification, the lock screen and the volume keys follow it; commands are routed the same way.
     */
    val onTv: StateFlow<PlayerState> = state
        .map { it.onTv() }
        .stateIn(scope, SharingStarted.Eagerly, PlayerState.EMPTY)

    val capabilities: StateFlow<Capabilities> = _backend
        .flatMapLatest { it?.capabilities ?: flowOf(Capabilities.NONE) }
        .stateIn(scope, SharingStarted.Eagerly, Capabilities.NONE)

    /**
     * The LAN is gone and Bluetooth could take over once the user grants "Nearby devices" or turns
     * Bluetooth on; `null` otherwise (see [CompanionBackend.bluetoothNeed]).
     */
    val bluetoothNeed: StateFlow<BluetoothStatus?> = _backend
        .flatMapLatest { (it as? CompanionBackend)?.bluetoothNeed ?: flowOf(null) }
        .stateIn(scope, SharingStarted.Eagerly, null)

    private val _events = MutableSharedFlow<PlayerEvent>(extraBufferCapacity = 16)
    val events: SharedFlow<PlayerEvent> = _events.asSharedFlow()

    private val _serviceRunning = MutableStateFlow(false)

    /** The foreground service runs; it keeps polling alive in the background. */
    val serviceRunning: StateFlow<Boolean> = _serviceRunning.asStateFlow()

    private var backendScope: CoroutineScope? = null
    private val connectMutex = Mutex()
    private var autoConnectJob: Job? = null

    /** Touchpad and keyboard, in the order they happened; one coroutine sends them ([pumpInput]). */
    private val inputs = Channel<PcInput>(Channel.UNLIMITED)

    init {
        scope.launch { pumpInput() }
        combine(appVisibility.visible, _serviceRunning) { visible, service -> visible || service }
            .onEach { active -> _backend.value?.setActive(active) }
            .launchIn(scope)
        appVisibility.visible
            .onEach { visible -> if (visible) _backend.value?.refresh() }
            .launchIn(scope)
        // Keep the active server object fresh (favorites, last folder, name edits).
        servers.serversState
            .onEach { list ->
                val active = _activeServer.value ?: return@onEach
                list.firstOrNull { it.id == active.id }?.let { fresh ->
                    if (fresh == active) return@let
                    _activeServer.value = fresh
                    // A Bluetooth-paired PC got its LAN address (or DHCP moved it): artwork needs the pin and token there.
                    if (fresh.host != active.host || fresh.tlsPort != active.tlsPort || fresh.certPin != active.certPin) {
                        authorizeArt(fresh, secrets.companionToken(fresh.id))
                    }
                }
            }
            .launchIn(scope)
    }

    /** Called by the foreground service when it starts and stops. */
    fun setServiceRunning(running: Boolean) {
        _serviceRunning.value = running
    }

    /** Connect to the last used server if nothing is connected yet (app start). */
    fun autoConnectLast() {
        if (_backend.value != null || autoConnectJob?.isActive == true) return
        autoConnectJob = scope.launch {
            val last = servers.lastServer() ?: return@launch
            connect(last)
        }
    }

    suspend fun connect(server: SavedServer) = connectMutex.withLock {
        disconnectInternal()
        val newScope = CoroutineScope(SupervisorJob() + scope.coroutineContext.minusKey(Job))
        backendScope = newScope
        val backend = createBackend(server, newScope)
        _activeServer.value = server
        _backend.value = backend
        backend.setActive(appVisibility.visible.value || _serviceRunning.value)
        servers.upsert(server.copy(lastUsedAt = System.currentTimeMillis()))
        backend.connect()
    }

    suspend fun disconnect() = connectMutex.withLock { disconnectInternal() }

    private suspend fun disconnectInternal() {
        val old = _backend.value ?: return
        _backend.value = null
        _activeServer.value = null
        try {
            resultOf { old.disconnect() }.onFailure { Log.d(TAG, "disconnect failed: ${it.message}") }
        } finally {
            backendScope?.cancel()
            backendScope = null
            artAuth.clear()
        }
    }

    private fun createBackend(server: SavedServer, backendScope: CoroutineScope): PlayerBackend {
        val token = secrets.companionToken(server.id)
        // The freshest copy of this server: discovery / welcome may fill in the LAN or Bluetooth address.
        val current = { _activeServer.value?.takeIf { it.id == server.id } ?: server }
        val backend = CompanionBackend(
            transports = transports,
            endpoint = { current().let { CompanionEndpoint(it.host, it.wsPort, it.btAddress, it.pcId, it.tlsPort, it.certPin) } },
            initialName = server.displayName,
            device = identity.deviceInfo(),
            credentials = CompanionCredentials(token = token),
            onToken = { newToken ->
                secrets.setCompanionToken(server.id, newToken)
                authorizeArt(current(), newToken)
                _events.tryEmit(PlayerEvent.Paired(server))
            },
            scope = backendScope,
            onWelcome = { welcome, pin ->
                // The port of the encrypted WebSocket (e.g. told over Bluetooth): the LAN link uses it from now on.
                welcome.tls?.port?.takeIf { pin == null && it != current().tlsPort }?.let { port ->
                    scope.launch { servers.update(server.id) { it.copy(tlsPort = port) } }
                }
                if (pin != null) {
                    authorizeArt(current().copy(tlsPort = pin.port, certPin = pin.sha256), secrets.companionToken(server.id))
                    scope.launch { servers.update(server.id) { it.copy(tlsPort = pin.port, certPin = pin.sha256) } }
                }
                val sighting = CompanionSighting(pcId = welcome.pcId, btAddress = welcome.bt, macs = welcome.macs)
                if (current().mergeSighting(sighting) != current()) {
                    scope.launch { servers.update(server.id) { it.mergeSighting(sighting) } }
                }
            },
            onLanFound = { lan ->
                val sighting = CompanionSighting(pcId = null, host = lan.host, wsPort = lan.port, btAddress = lan.btAddress)
                scope.launch { servers.update(server.id) { it.mergeSighting(sighting) } }
            },
            // A PC known only by Bluetooth is always reached over it; otherwise the user setting decides.
            bluetoothAllowed = { settings.settings.value.bluetoothFallback || current().host.isBlank() },
        )
        authorizeArt(server, token)
        return backend
    }

    /** The pairing flow got a token itself (before [connect]); show the usual "paired" notice. */
    fun announcePaired(server: SavedServer) {
        _events.tryEmit(PlayerEvent.Paired(server))
    }

    // ------------------------------------------------------------------ commands

    /** Fire-and-forget; failures are reported through [events]. */
    fun send(command: PlayerCommand) {
        scope.launch { sendAwait(command) }
    }

    /** Transport and volume go to YouTube while it is open on the TV ([PlayerState.routeToYouTube]). */
    suspend fun sendAwait(command: PlayerCommand): Result<Unit> {
        state.value.routeToYouTube(command, System.currentTimeMillis())?.let { return sendSystemAwait(it) }
        return withBackend { it.send(command) }.onFailure { e ->
            Log.d(TAG, "command $command failed: ${e.message}")
            _events.tryEmit(PlayerEvent.CommandFailed(command, e))
        }
    }

    /** Like [sendAwait] but without the "command failed" event: the caller reports the outcome itself. */
    suspend fun sendQuiet(command: PlayerCommand): Result<Unit> = withBackend { it.send(command) }

    fun sendSystem(command: SystemCommand) {
        scope.launch { sendSystemAwait(command) }
    }

    suspend fun sendSystemAwait(command: SystemCommand): Result<Unit> =
        withBackend { it.sendSystem(command) }.onFailure { e ->
            Log.d(TAG, "system command $command failed: ${e.message}")
            _events.tryEmit(PlayerEvent.SystemCommandFailed(command, e))
        }

    /** Touchpad and keyboard (`input` capability): fire-and-forget, in order; moves are merged to one per frame. */
    fun input(input: PcInput) {
        inputs.trySend(input)
    }

    /**
     * Sends [inputs] one after another (so a button's press and release never swap). What piles up while a frame's
     * move is sent and the frame passes is merged ([InputBatch]): at most one move and one scroll per frame (as often as
     * a 120 Hz screen reports the finger).
     */
    private suspend fun pumpInput() {
        val batch = InputBatch()
        while (true) {
            var next: PcInput? = inputs.receive()
            while (next != null) {
                batch.add(next)
                next = inputs.tryReceive().getOrNull()
            }
            val sent = batch.drain()
            sent.forEach { _backend.value?.sendInput(it) }
            if (sent.any { it is PcInput.Move || it is PcInput.Scroll }) delay(INPUT_FRAME_MS)
        }
    }

    suspend fun audioDevices(): Result<List<AudioDevice>> = withBackend { it.audioDevices() }

    suspend fun listDisplays(): Result<DisplayList> = withBackend { it.listDisplays() }

    /** The computers paired in Moonlight on the PC; with [refresh] each is asked for its apps first (~15 s at most). */
    suspend fun moonlightHosts(refresh: Boolean): Result<List<MoonlightHost>> = withBackend { it.moonlightHosts(refresh) }

    /** What was just started through Moonlight on the connected PC: preselected next time (kept with the saved PC). */
    fun rememberMoonlight(choice: MoonlightChoice) {
        val server = _activeServer.value ?: return
        if (server.moonlight == choice) return
        scope.launch { servers.update(server.id) { it.copy(moonlight = choice) } }
    }

    /** Hardware volume keys: ±[delta] VLC units (the PC's volume while YouTube is on the TV), then report the percentage. */
    fun adjustVolume(delta: Int) {
        scope.launch {
            val before = onTv.value.volume.value
            val target = VlcVolume.clamp(before + delta)
            val result = sendAwait(PlayerCommand.SetVolume(target))
            if (result.isSuccess) {
                _events.tryEmit(PlayerEvent.VolumeChanged(onTv.value.volume.percent))
            }
        }
    }

    suspend fun browse(uri: String): Result<List<Entry>> = withBackend { it.browse(uri) }

    suspend fun playlist(): Result<List<PlaylistItem>> = withBackend { it.playlist() }

    // ------------------------------------------------------------------ watch history (`history` capability)

    suspend fun history(filter: HistoryFilter, limit: Int, offset: Int = 0): Result<HistoryPage> =
        withBackend { it.history(filter, limit, offset) }

    /** Failures are reported through [events]. */
    suspend fun updateHistory(uri: String, change: HistoryChange): Result<Unit> =
        withBackend { it.updateHistory(uri, change) }.onFailure { _events.tryEmit(PlayerEvent.RequestFailed(it)) }

    /** Failures are reported through [events]. */
    suspend fun clearHistory(): Result<Unit> =
        withBackend { it.clearHistory() }.onFailure { _events.tryEmit(PlayerEvent.RequestFailed(it)) }

    // ------------------------------------------------------------------ library (`library` capability)

    suspend fun library(): Result<Library> = withBackend { it.library() }

    suspend fun show(key: String): Result<ShowDetails> = withBackend { it.show(key) }

    /**
     * The artwork comes from the encrypted listener of [server] (its host and `tls.port`, as the link rewrites `artUrl`)
     * over https with the companion's own certificate, once it is pinned, and the token.
     */
    private fun authorizeArt(server: SavedServer, token: String?) {
        if (server.host.isBlank()) return
        val port = lanRoute(server.tlsPort, server.certPin).port
        server.certPin?.let { pinned.set(server.host, port, it) }
        token?.let { artAuth.set(server.host, port, "Bearer $it") }
    }

    /** Reconnect now instead of waiting for the backoff (Bluetooth permission granted, radio on). */
    fun retryNow() {
        (_backend.value as? CompanionBackend)?.retryNow()
    }

    /** [block] on the active backend, or a "not connected" failure. */
    private inline fun <T> withBackend(block: (PlayerBackend) -> Result<T>): Result<T> =
        _backend.value?.let(block) ?: Result.failure(IllegalStateException("not connected"))

    private companion object {
        const val TAG = "PlayerController"

        /** A display frame at 60 Hz: the touchpad sends at most one move per frame. */
        const val INPUT_FRAME_MS = 8L
    }
}
