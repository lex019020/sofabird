package dev.vlcremote.app.data.companion

import android.util.Log
import dev.vlcremote.protocol.backend.PlayerBackend
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.backend.SystemCommand
import dev.vlcremote.protocol.companion.BtAddress
import dev.vlcremote.protocol.companion.CommandMapping
import dev.vlcremote.protocol.companion.CompanionErrorException
import dev.vlcremote.protocol.companion.CompanionMessage
import dev.vlcremote.protocol.companion.DecodedMessage
import dev.vlcremote.protocol.companion.DeviceInfo
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.HistoryChange
import dev.vlcremote.protocol.model.HistoryFilter
import dev.vlcremote.protocol.model.HistoryPage
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlaylistItem
import dev.vlcremote.protocol.model.TransportKind
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.cancellation.CancellationException
import kotlin.random.Random

/** Where the companion can be reached; read again before every connection attempt. */
data class CompanionEndpoint(
    /** LAN address; blank for a PC known only by Bluetooth. */
    val host: String,
    val port: Int,
    val btAddress: String? = null,
    val pcId: String? = null,
)

/** Timeouts and intervals of [CompanionBackend] (protocol/README.md "Keep-alive", "Bluetooth"). */
data class CompanionTiming(
    val wsPingIntervalMs: Long = 30_000,
    val btPingIntervalMs: Long = 15_000,
    val pongTimeoutMs: Long = 10_000,
    val requestTimeoutMs: Long = 10_000,
    val commandBufferMs: Long = 5_000,
    /** WebSocket connect timeout before falling back to Bluetooth ("the PC does not answer for ~3 s"). */
    val lanConnectTimeoutMs: Long = 3_000,
    /** While on RFCOMM: how often to look whether the LAN is back. */
    val lanProbeIntervalMs: Long = 20_000,
    /** On switching back to the LAN: how long to let replies to in-flight requests arrive on the old link. */
    val switchDrainMs: Long = 1_000,
)

/**
 * Level 2: session with the Windows companion over a [CompanionTransport] — WebSocket on the LAN,
 * RFCOMM when the LAN is unavailable. `hello`/`welcome` with token or pairing code, pushed `state`,
 * request/response by `id`, ping every 30 s (WS) / 15 s (RFCOMM) with reconnect when no pong in
 * 10 s, exponential backoff, commands buffered up to 5 s while reconnecting.
 *
 * Link choice on every (re)connect: WebSocket first (~3 s); when it fails and the PC's Bluetooth
 * address is known, Bluetooth is allowed, on, permitted and the PC bonded → RFCOMM. While on RFCOMM
 * the LAN is probed every ~20 s; when the companion answers there, a WebSocket session is opened
 * and handshaken *before* the RFCOMM link is closed, so the remote never shows "disconnected".
 */
class CompanionBackend(
    private val transports: CompanionTransports,
    private val endpoint: () -> CompanionEndpoint,
    initialName: String,
    private val device: DeviceInfo,
    credentials: CompanionCredentials,
    /** Called with the token issued after a successful pairing; persist it. */
    private val onToken: (String) -> Unit,
    private val scope: CoroutineScope,
    /** Every `welcome` (persist `bt`, the GUID). */
    private val onWelcome: (CompanionMessage.Welcome) -> Unit = {},
    /** The LAN probe found the companion at another address than [endpoint] says (persist it). */
    private val onLanFound: (LanEndpoint) -> Unit = {},
    /** User setting "Use Bluetooth when Wi-Fi is unavailable". */
    private val bluetoothAllowed: () -> Boolean = { true },
    private val timing: CompanionTiming = CompanionTiming(),
) : PlayerBackend {

    private val _capabilities = MutableStateFlow(Capabilities.NONE)
    override val capabilities: StateFlow<Capabilities> = _capabilities.asStateFlow()

    private val _state = MutableStateFlow(PlayerState.EMPTY)
    override val state: StateFlow<PlayerState> = _state.asStateFlow()

    private val _connection = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    override val connection: StateFlow<ConnectionState> = _connection.asStateFlow()

    private val _events = MutableSharedFlow<CompanionMessage.Event>(extraBufferCapacity = 16)
    val events: SharedFlow<CompanionMessage.Event> = _events.asSharedFlow()

    /**
     * The LAN is down and Bluetooth could take over, but the user has to act first: grant
     * "Nearby devices" ([BluetoothStatus.NO_PERMISSION]) or turn Bluetooth on ([BluetoothStatus.OFF]).
     * `null` otherwise. Then call [retryNow].
     */
    private val _bluetoothNeed = MutableStateFlow<BluetoothStatus?>(null)
    val bluetoothNeed: StateFlow<BluetoothStatus?> = _bluetoothNeed.asStateFlow()

    @Volatile var hostName: String = initialName
        private set

    @Volatile private var credentials = credentials
    private val active = MutableStateFlow(true)
    private val ids = AtomicLong(1)
    private val pending = ConcurrentHashMap<String, CompletableDeferred<CompanionMessage>>()
    private val wake = Channel<Unit>(Channel.CONFLATED)

    /** The link requests go to; swapped atomically when switching back to the LAN. */
    @Volatile private var transport: CompanionTransport? = null
    @Volatile private var currentKind: TransportKind = TransportKind.WIFI
    private var sessionJob: Job? = null
    @Volatile private var pongDeferred: CompletableDeferred<Unit>? = null

    private class Link(val transport: CompanionTransport, val welcome: CompanionMessage.Welcome)

    override suspend fun connect() {
        if (sessionJob?.isActive == true) return
        _connection.value = ConnectionState.Connecting(0)
        sessionJob = scope.launch { sessionLoop() }
    }

    override suspend fun disconnect() {
        val job = sessionJob
        sessionJob = null
        transport?.let { closeQuietly(it) } // unblocks a blocking RFCOMM read first
        job?.cancelAndJoin()
        transport = null
        failPending(CancellationException("disconnected"))
        _bluetoothNeed.value = null
        _connection.value = ConnectionState.Disconnected
    }

    override fun setActive(active: Boolean) {
        this.active.value = active
    }

    /** Skip the current backoff pause (Bluetooth permission granted, radio turned on…). */
    fun retryNow() {
        wake.trySend(Unit)
    }

    override suspend fun refresh() {
        val t = transport ?: return
        try {
            t.sendMessage(CompanionMessage.GetState(nextId()))
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
        }
    }

    // ------------------------------------------------------------------ session

    private suspend fun sessionLoop() {
        var attempt = 0
        var lastError: ConnectionError? = null
        while (currentCoroutineContext().isActive) {
            active.first { it }
            _connection.value = ConnectionState.Connecting(attempt, lastError)
            try {
                runSession()
                // Clean close from the other side: reconnect quickly.
                lastError = ConnectionError.Other("closed")
                attempt = 0
            } catch (e: CancellationException) {
                throw e
            } catch (e: CompanionHandshakeException) {
                if (e.error.isTerminalHandshakeError) {
                    _connection.value = ConnectionState.Failed(e.error)
                    return // nothing to retry until the user acts
                }
                lastError = e.error
            } catch (e: Throwable) {
                lastError = classifyCompanionError(e)
                Log.d(TAG, "session failed: $lastError", e)
            } finally {
                transport?.let { closeQuietly(it) }
                transport = null
                failPending(IllegalStateException("connection lost"))
            }
            if (_connection.value !is ConnectionState.Failed) {
                _connection.value = ConnectionState.Connecting(attempt + 1, lastError)
            }
            withTimeoutOrNull(backoff(attempt++)) { wake.receive() }
        }
    }

    private fun backoff(attempt: Int): Long {
        val base = (1_000L shl attempt.coerceAtMost(5)).coerceAtMost(30_000L)
        return base + Random.nextLong(0, 750)
    }

    private suspend fun runSession() {
        var link = openLink()
        while (true) {
            link = runLink(link) ?: return
        }
    }

    /** WebSocket first; RFCOMM when the LAN fails and Bluetooth may be used. */
    private suspend fun openLink(): Link {
        val ep = endpoint()
        var lanFailure: Throwable? = null
        if (ep.host.isNotBlank()) {
            try {
                tryLan(ep.host, ep.port)?.let { return it }
                lanFailure = CompanionHandshakeException(ConnectionError.Timeout, "no answer on ${ep.host}:${ep.port}")
            } catch (e: CancellationException) {
                throw e
            } catch (e: CompanionHandshakeException) {
                if (e.error.isTerminalHandshakeError) throw e // the same PC would say the same over RFCOMM
                lanFailure = e
            } catch (e: Throwable) {
                lanFailure = e
            }
        }

        val bt = BtAddress.normalize(ep.btAddress)
        if (bt == null || !bluetoothAllowed()) {
            _bluetoothNeed.value = null
            throw lanFailure ?: CompanionHandshakeException(ConnectionError.BluetoothFailed, "no LAN address and no Bluetooth")
        }
        val status = transports.bluetoothStatus(bt)
        if (status != BluetoothStatus.READY) {
            _bluetoothNeed.value = status.takeIf { it.userCanFix }
            Log.d(TAG, "LAN failed, Bluetooth not usable: $status")
            throw lanFailure ?: CompanionHandshakeException(ConnectionError.BluetoothFailed, "bluetooth $status")
        }
        _bluetoothNeed.value = null
        val rfcomm = try {
            transports.openRfcomm(bt)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            Log.d(TAG, "RFCOMM to $bt failed: ${e.message}")
            // A dead LAN explains more than "Bluetooth failed" when the phone normally uses Wi-Fi.
            throw lanFailure ?: e
        }
        return try {
            Link(rfcomm, handshakeOn(rfcomm))
        } catch (e: Throwable) {
            closeQuietly(rfcomm)
            throw e
        }
    }

    /** WebSocket + handshake; `null` when the PC did not answer within the LAN timeout. */
    private suspend fun tryLan(host: String, port: Int): Link? {
        val ws = withTimeoutOrNull(timing.lanConnectTimeoutMs) { transports.openWebSocket(host, port) } ?: return null
        return try {
            Link(ws, handshakeOn(ws))
        } catch (e: Throwable) {
            closeQuietly(ws)
            throw e
        }
    }

    private suspend fun handshakeOn(t: CompanionTransport): CompanionMessage.Welcome {
        val creds = credentials
        return t.handshake(CompanionMessage.Hello(device = device, token = creds.token, pairCode = creds.pairCode))
    }

    private fun applyWelcome(link: Link) {
        val welcome = link.welcome
        val creds = credentials
        welcome.token?.let { token ->
            credentials = CompanionCredentials(token = token)
            onToken(token)
        }
        if (creds.pairCode != null && welcome.token == null && creds.token == null) {
            // Paired but no token returned: we can keep this session, but cannot reconnect.
            Log.w(TAG, "welcome without token after pairing")
        }
        hostName = welcome.name
        currentKind = link.transport.kind
        transport = link.transport
        _capabilities.value = Capabilities.fromWire(welcome.caps)
        _state.value = _state.value.copy(vlc = welcome.vlc.copy(level = 2))
        _bluetoothNeed.value = null
        _connection.value = ConnectionState.Connected(hostName, level = 2, transport = currentKind)
        wake.tryReceive() // a stale "retry now" must not skip a future backoff
        runCatching { onWelcome(welcome) }.onFailure { Log.w(TAG, "onWelcome failed", it) }
    }

    /** Runs one link until it closes (`null`) or the LAN came back while on RFCOMM (the new link). */
    private suspend fun runLink(link: Link): Link? {
        val t = link.transport
        applyWelcome(link)
        t.sendMessage(CompanionMessage.GetState(nextId()))
        return coroutineScope {
            val outcome = CompletableDeferred<Link?>()
            val reader = launch {
                readLoop(t)
                outcome.complete(null)
            }
            val pinger = launch { pingLoop(t) }
            val prober = if (t.kind == TransportKind.BLUETOOTH) {
                launch {
                    val lan = lanProbeLoop()
                    if (!outcome.complete(lan)) closeQuietly(lan.transport)
                }
            } else {
                null
            }
            val next = outcome.await()
            pinger.cancel()
            prober?.cancel()
            if (next != null) {
                Log.i(TAG, "LAN is back, leaving Bluetooth")
                transport = next.transport // new requests already go over the LAN
                withTimeoutOrNull(timing.switchDrainMs) { while (pending.isNotEmpty()) delay(DRAIN_POLL_MS) }
                closeQuietly(t) // the reader sees the end of the old link
            }
            reader.join()
            next
        }
    }

    private suspend fun readLoop(t: CompanionTransport) {
        while (true) {
            val decoded = t.receiveMessage() ?: return
            val msg = (decoded as? DecodedMessage.Known)?.message ?: continue
            when (msg) {
                is CompanionMessage.State -> {
                    var s = msg.toPlayerState()
                    if (s.playback.updatedAt <= 0) {
                        s = s.copy(playback = s.playback.copy(updatedAt = System.currentTimeMillis()))
                    }
                    _state.value = s
                    if (_connection.value !is ConnectionState.Connected && transport === t) {
                        _connection.value = ConnectionState.Connected(hostName, 2, transport = t.kind)
                    }
                }
                is CompanionMessage.Pong -> pongDeferred?.complete(Unit)
                is CompanionMessage.Ping -> t.sendMessage(CompanionMessage.Pong())
                is CompanionMessage.Event -> {
                    _events.tryEmit(msg)
                    if (msg.name == "vlcExited") {
                        _state.value = _state.value.copy(vlc = _state.value.vlc.exited())
                    }
                }
                else -> {
                    val id = msg.id
                    if (id != null) pending.remove(id)?.complete(msg)
                }
            }
        }
    }

    private suspend fun pingLoop(t: CompanionTransport) {
        val interval = if (t.kind == TransportKind.BLUETOOTH) timing.btPingIntervalMs else timing.wsPingIntervalMs
        while (true) {
            delay(interval)
            val deferred = CompletableDeferred<Unit>()
            pongDeferred = deferred
            t.sendMessage(CompanionMessage.Ping())
            val ok = withTimeoutOrNull(timing.pongTimeoutMs) { deferred.await() } != null
            if (!ok) {
                Log.d(TAG, "no pong in ${timing.pongTimeoutMs} ms over ${t.kind}, reconnecting")
                _connection.value = ConnectionState.Degraded(hostName, 1, t.kind)
                closeQuietly(t) // ends readLoop → runLink returns → reconnect
                return
            }
        }
    }

    /** While on RFCOMM: wait for the companion to answer on the LAN, then open and handshake a WebSocket. */
    private suspend fun lanProbeLoop(): Link {
        while (true) {
            delay(timing.lanProbeIntervalMs)
            val ep = endpoint()
            val found = try {
                transports.probeLan(ep.host, ep.port, ep.pcId, ep.btAddress)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                null
            } ?: continue
            if (!found.host.equals(ep.host, ignoreCase = true) || found.port != ep.port) {
                runCatching { onLanFound(found) }
            }
            val link = try {
                tryLan(found.host, found.port)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                Log.d(TAG, "LAN answered /info but the WebSocket failed: ${e.message}")
                null
            }
            if (link != null) return link
        }
    }

    private suspend fun closeQuietly(t: CompanionTransport) {
        withContext(NonCancellable) { runCatching { t.close() } }
    }

    private fun failPending(cause: Throwable) {
        val snapshot = pending.values.toList()
        pending.clear()
        snapshot.forEach { it.completeExceptionally(cause) }
    }

    private fun nextId(): String = ids.getAndIncrement().toString()

    // ------------------------------------------------------------------ requests

    private suspend fun request(build: (String) -> CompanionMessage): CompanionMessage {
        if (!awaitOnline()) throw CompanionHandshakeException(ConnectionError.Timeout, "not connected")
        val t = transport ?: throw IllegalStateException("no session")
        val id = nextId()
        val deferred = CompletableDeferred<CompanionMessage>()
        pending[id] = deferred
        try {
            t.sendMessage(build(id))
            val reply = withTimeout(timing.requestTimeoutMs) { deferred.await() }
            if (reply is CompanionMessage.Err) {
                throw CompanionErrorException(reply.errorCode, reply.code, reply.msg)
            }
            return reply
        } finally {
            pending.remove(id)
        }
    }

    private suspend fun awaitOnline(): Boolean {
        val c = connection.value
        if (c is ConnectionState.Connected || c is ConnectionState.Degraded) return transport != null
        if (c is ConnectionState.Failed || c is ConnectionState.Disconnected) return false
        return withTimeoutOrNull(timing.commandBufferMs) {
            connection.first { it is ConnectionState.Connected || it is ConnectionState.Failed }
        } is ConnectionState.Connected
    }

    override suspend fun send(command: PlayerCommand): Result<Unit> = runCatching {
        request { id -> CommandMapping.toCmd(id, command) }
        Unit
    }

    override suspend fun sendSystem(command: SystemCommand): Result<Unit> = runCatching {
        request { id -> CommandMapping.toSys(id, command) }
        Unit
    }

    /** `listDisplays` returns data, not just `ok`. */
    override suspend fun listDisplays(): Result<dev.vlcremote.protocol.companion.DisplayList> = runCatching {
        val reply = request { id -> CommandMapping.toSys(id, SystemCommand.ListDisplays) }
        (reply as? CompanionMessage.Displays)?.toList() ?: dev.vlcremote.protocol.companion.DisplayList()
    }

    override suspend fun audioDevices(): Result<List<dev.vlcremote.protocol.companion.AudioDevice>> = runCatching {
        val reply = request { id -> CompanionMessage.GetAudioDevices(id) }
        (reply as? CompanionMessage.AudioDevices)?.items ?: emptyList()
    }

    override suspend fun browse(uri: String): Result<List<Entry>> = runCatching {
        val reply = request { id -> CompanionMessage.Browse(id, uri) }
        (reply as? CompanionMessage.Entries)?.items ?: error("unexpected reply ${reply::class.simpleName}")
    }

    override suspend fun playlist(): Result<List<PlaylistItem>> = runCatching {
        val reply = request { id -> CompanionMessage.GetPlaylist(id) }
        (reply as? CompanionMessage.Playlist)?.items ?: error("unexpected reply ${reply::class.simpleName}")
    }

    override suspend fun history(filter: HistoryFilter, limit: Int, offset: Int): Result<HistoryPage> = runCatching {
        val reply = request { id -> CompanionMessage.GetHistory(id, filter.wire, limit, offset) }
        val history = reply as? CompanionMessage.History ?: error("unexpected reply ${reply::class.simpleName}")
        HistoryPage(history.items, history.total)
    }

    override suspend fun updateHistory(uri: String, change: HistoryChange): Result<Unit> = runCatching {
        request { id ->
            when (change) {
                is HistoryChange.SetFinished -> CompanionMessage.HistoryUpdate(id, uri, finished = change.finished)
                HistoryChange.Remove -> CompanionMessage.HistoryUpdate(id, uri, remove = true)
            }
        }
        Unit
    }

    override suspend fun clearHistory(): Result<Unit> = runCatching {
        request { id -> CompanionMessage.HistoryClear(id) }
        Unit
    }

    /** Bearer header for the companion's `/art` proxy. */
    fun authorizationHeader(): String? = credentials.token?.let { "Bearer $it" }

    private companion object {
        const val TAG = "CompanionBackend"
        const val DRAIN_POLL_MS = 50L
    }
}
