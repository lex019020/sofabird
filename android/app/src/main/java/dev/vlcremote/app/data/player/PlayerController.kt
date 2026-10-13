package dev.vlcremote.app.data.player

import android.util.Log
import dev.vlcremote.app.data.companion.BluetoothStatus
import dev.vlcremote.app.data.companion.CompanionBackend
import dev.vlcremote.app.data.companion.CompanionCredentials
import dev.vlcremote.app.data.companion.CompanionEndpoint
import dev.vlcremote.app.data.companion.CompanionTransports
import dev.vlcremote.app.data.net.ArtAuth
import dev.vlcremote.app.data.secrets.SecretStore
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.data.vlc.VlcHttpApi
import dev.vlcremote.app.data.vlc.VlcHttpBackend
import dev.vlcremote.app.domain.model.CompanionSighting
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.domain.model.mergeSighting
import dev.vlcremote.protocol.backend.PlayerBackend
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.backend.SystemCommand
import dev.vlcremote.protocol.companion.AudioDevice
import dev.vlcremote.protocol.companion.DisplayList
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.HistoryChange
import dev.vlcremote.protocol.model.HistoryFilter
import dev.vlcremote.protocol.model.HistoryPage
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlaylistItem
import dev.vlcremote.protocol.model.VlcVolume
import io.ktor.client.HttpClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
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
    private val ktor: HttpClient,
    private val servers: ServerRepository,
    private val secrets: SecretStore,
    private val artAuth: ArtAuth,
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

    /** Set by the foreground service while it runs; keeps polling alive in the background. */
    val serviceRunning = MutableStateFlow(false)

    private var backendScope: CoroutineScope? = null
    private val connectMutex = Mutex()
    private var autoConnectJob: Job? = null

    init {
        combine(appVisibility.visible, serviceRunning) { visible, service -> visible || service }
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
                    // A Bluetooth-paired PC got its LAN address (or DHCP moved it): artwork needs the token there.
                    val addressChanged = fresh.host != active.host || fresh.wsPort != active.wsPort
                    if (fresh.isCompanion && fresh.host.isNotBlank() && addressChanged) {
                        secrets.companionToken(fresh.id)?.let { artAuth.set(fresh.host, fresh.wsPort, "Bearer $it") }
                    }
                }
            }
            .launchIn(scope)
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
        backend.setActive(appVisibility.visible.value || serviceRunning.value)
        servers.upsert(server.copy(lastUsedAt = System.currentTimeMillis()))
        backend.connect()
    }

    suspend fun disconnect() = connectMutex.withLock { disconnectInternal() }

    private suspend fun disconnectInternal() {
        val old = _backend.value ?: return
        _backend.value = null
        _activeServer.value = null
        runCatching { old.disconnect() }
        backendScope?.cancel()
        backendScope = null
        artAuth.clear()
    }

    private fun createBackend(server: SavedServer, backendScope: CoroutineScope): PlayerBackend = when (server.mode) {
        ServerMode.DIRECT -> {
            val api = VlcHttpApi(ktor, server.host, server.httpPort, secrets.vlcPassword(server.id).orEmpty())
            artAuth.set(server.host, server.httpPort, api.authorizationHeader())
            VlcHttpBackend(api, server.displayName, backendScope)
        }
        ServerMode.COMPANION -> {
            val token = secrets.companionToken(server.id)
            // The freshest copy of this server: discovery / welcome may fill in the LAN or Bluetooth address.
            val current = { _activeServer.value?.takeIf { it.id == server.id } ?: server }
            val backend = CompanionBackend(
                transports = transports,
                endpoint = { current().let { CompanionEndpoint(it.host, it.wsPort, it.btAddress, it.pcId) } },
                initialName = server.displayName,
                device = identity.deviceInfo(),
                credentials = CompanionCredentials(token = token),
                onToken = { newToken ->
                    secrets.setCompanionToken(server.id, newToken)
                    current().takeIf { it.host.isNotBlank() }?.let { artAuth.set(it.host, it.wsPort, "Bearer $newToken") }
                    _events.tryEmit(PlayerEvent.Paired(server))
                },
                scope = backendScope,
                onWelcome = { welcome ->
                    val sighting = CompanionSighting(pcId = welcome.pcId, btAddress = welcome.bt)
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
            if (token != null && server.host.isNotBlank()) artAuth.set(server.host, server.wsPort, "Bearer $token")
            backend
        }
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

    suspend fun sendAwait(command: PlayerCommand): Result<Unit> {
        val backend = _backend.value ?: return Result.failure(IllegalStateException("not connected"))
        val result = backend.send(command)
        result.onFailure { e ->
            Log.d(TAG, "command $command failed: ${e.message}")
            _events.tryEmit(PlayerEvent.CommandFailed(command, e))
        }
        return result
    }

    /** Like [sendAwait] but without the "command failed" event: the caller reports the outcome itself. */
    suspend fun sendQuiet(command: PlayerCommand): Result<Unit> =
        _backend.value?.send(command) ?: Result.failure(IllegalStateException("not connected"))

    fun sendSystem(command: SystemCommand) {
        scope.launch { sendSystemAwait(command) }
    }

    suspend fun sendSystemAwait(command: SystemCommand): Result<Unit> {
        val backend = _backend.value ?: return Result.failure(IllegalStateException("not connected"))
        val result = backend.sendSystem(command)
        result.onFailure { e ->
            Log.d(TAG, "system command $command failed: ${e.message}")
            _events.tryEmit(PlayerEvent.SystemCommandFailed(command, e))
        }
        return result
    }

    suspend fun audioDevices(): Result<List<AudioDevice>> =
        _backend.value?.audioDevices() ?: Result.failure(IllegalStateException("not connected"))

    suspend fun listDisplays(): Result<DisplayList> =
        _backend.value?.listDisplays() ?: Result.failure(IllegalStateException("not connected"))

    /** Hardware volume keys: ±[delta] VLC units, then report the resulting percentage. */
    fun adjustVolume(delta: Int) {
        scope.launch {
            val before = state.value.volume.value
            val target = VlcVolume.clamp(before + delta)
            val result = sendAwait(PlayerCommand.SetVolume(target))
            if (result.isSuccess) {
                _events.tryEmit(PlayerEvent.VolumeChanged(state.value.volume.percent))
            }
        }
    }

    suspend fun browse(uri: String): Result<List<Entry>> =
        _backend.value?.browse(uri) ?: Result.failure(IllegalStateException("not connected"))

    suspend fun playlist(): Result<List<PlaylistItem>> =
        _backend.value?.playlist() ?: Result.failure(IllegalStateException("not connected"))

    // ------------------------------------------------------------------ watch history (`history` capability)

    suspend fun history(filter: HistoryFilter, limit: Int, offset: Int = 0): Result<HistoryPage> =
        _backend.value?.history(filter, limit, offset) ?: Result.failure(IllegalStateException("not connected"))

    /** Failures are reported through [events]. */
    suspend fun updateHistory(uri: String, change: HistoryChange): Result<Unit> {
        val result = _backend.value?.updateHistory(uri, change) ?: Result.failure(IllegalStateException("not connected"))
        result.onFailure { _events.tryEmit(PlayerEvent.RequestFailed(it)) }
        return result
    }

    /** Failures are reported through [events]. */
    suspend fun clearHistory(): Result<Unit> {
        val result = _backend.value?.clearHistory() ?: Result.failure(IllegalStateException("not connected"))
        result.onFailure { _events.tryEmit(PlayerEvent.RequestFailed(it)) }
        return result
    }

    suspend fun refresh() {
        _backend.value?.refresh()
    }

    fun companionBackend(): CompanionBackend? = _backend.value as? CompanionBackend

    /** Reconnect now instead of waiting for the backoff (Bluetooth permission granted, radio on). */
    fun retryNow() {
        companionBackend()?.retryNow()
    }

    private companion object {
        const val TAG = "PlayerController"
    }
}
