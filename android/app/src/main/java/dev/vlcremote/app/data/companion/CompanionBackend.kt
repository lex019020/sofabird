package dev.vlcremote.app.data.companion

import android.util.Log
import dev.vlcremote.app.data.companion.CompanionSocket.closeQuietly
import dev.vlcremote.app.data.companion.CompanionSocket.handshake
import dev.vlcremote.app.data.companion.CompanionSocket.receiveMessage
import dev.vlcremote.app.data.companion.CompanionSocket.sendMessage
import dev.vlcremote.protocol.backend.PlayerBackend
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.backend.SystemCommand
import dev.vlcremote.protocol.companion.CommandMapping
import dev.vlcremote.protocol.companion.CompanionErrorException
import dev.vlcremote.protocol.companion.CompanionMessage
import dev.vlcremote.protocol.companion.DecodedMessage
import dev.vlcremote.protocol.companion.DeviceInfo
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlaylistItem
import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
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
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.cancellation.CancellationException
import kotlin.random.Random

/**
 * Level 2: WebSocket session with the Windows companion. `hello`/`welcome` with token or pairing
 * code, pushed `state`, request/response by `id`, ping every 30 s (reconnect when no pong in 10 s),
 * exponential backoff, commands buffered up to 5 s while reconnecting.
 */
class CompanionBackend(
    private val client: HttpClient,
    private val host: String,
    private val port: Int,
    initialName: String,
    private val device: DeviceInfo,
    credentials: CompanionCredentials,
    /** Called with the token issued after a successful pairing; persist it. */
    private val onToken: (String) -> Unit,
    private val scope: CoroutineScope,
    private val pingIntervalMs: Long = 30_000,
    private val pongTimeoutMs: Long = 10_000,
    private val requestTimeoutMs: Long = 10_000,
    private val commandBufferMs: Long = 5_000,
) : PlayerBackend {

    private val _capabilities = MutableStateFlow(Capabilities.NONE)
    override val capabilities: StateFlow<Capabilities> = _capabilities.asStateFlow()

    private val _state = MutableStateFlow(PlayerState.EMPTY)
    override val state: StateFlow<PlayerState> = _state.asStateFlow()

    private val _connection = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    override val connection: StateFlow<ConnectionState> = _connection.asStateFlow()

    private val _events = MutableSharedFlow<CompanionMessage.Event>(extraBufferCapacity = 16)
    val events: SharedFlow<CompanionMessage.Event> = _events.asSharedFlow()

    @Volatile var hostName: String = initialName
        private set

    @Volatile private var credentials = credentials
    private val active = MutableStateFlow(true)
    private val ids = AtomicLong(1)
    private val pending = ConcurrentHashMap<String, CompletableDeferred<CompanionMessage>>()

    @Volatile private var session: DefaultClientWebSocketSession? = null
    private var sessionJob: Job? = null
    private var pongDeferred: CompletableDeferred<Unit>? = null

    override suspend fun connect() {
        if (sessionJob?.isActive == true) return
        _connection.value = ConnectionState.Connecting(0)
        sessionJob = scope.launch { sessionLoop() }
    }

    override suspend fun disconnect() {
        sessionJob?.cancelAndJoin()
        sessionJob = null
        session?.closeQuietly()
        session = null
        failPending(CancellationException("disconnected"))
        _connection.value = ConnectionState.Disconnected
    }

    override fun setActive(active: Boolean) {
        this.active.value = active
    }

    override suspend fun refresh() {
        val s = session ?: return
        runCatching { s.sendMessage(CompanionMessage.GetState(nextId())) }
    }

    // ------------------------------------------------------------------ session

    private suspend fun sessionLoop() {
        var attempt = 0
        var lastError: ConnectionError? = null
        while (scope.isActive) {
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
                if (e.error is ConnectionError.PairingRequired ||
                    e.error is ConnectionError.InvalidPairCode ||
                    e.error is ConnectionError.ProtocolMismatch
                ) {
                    _connection.value = ConnectionState.Failed(e.error)
                    return // nothing to retry until the user acts
                }
                lastError = e.error
            } catch (e: Throwable) {
                lastError = CompanionSocket.classify(e)
                Log.d(TAG, "session failed: $lastError", e)
            } finally {
                session?.closeQuietly()
                session = null
                failPending(IllegalStateException("connection lost"))
            }
            if (_connection.value !is ConnectionState.Failed) {
                _connection.value = ConnectionState.Connecting(attempt + 1, lastError)
            }
            delay(backoff(attempt++))
        }
    }

    private fun backoff(attempt: Int): Long {
        val base = (1_000L shl attempt.coerceAtMost(5)).coerceAtMost(30_000L)
        return base + Random.nextLong(0, 750)
    }

    private suspend fun runSession() {
        val ws = CompanionSocket.open(client, host, port)
        session = ws
        val creds = credentials
        val welcome = ws.handshake(
            CompanionMessage.Hello(device = device, token = creds.token, pairCode = creds.pairCode),
        )
        welcome.token?.let { token ->
            credentials = CompanionCredentials(token = token)
            onToken(token)
        }
        if (creds.pairCode != null && welcome.token == null && creds.token == null) {
            // Paired but no token returned: we can keep this session, but cannot reconnect.
            Log.w(TAG, "welcome without token after pairing")
        }
        hostName = welcome.name
        _capabilities.value = Capabilities.fromWire(welcome.caps)
        _state.value = _state.value.copy(vlc = welcome.vlc.copy(level = 2))
        _connection.value = ConnectionState.Connected(hostName, level = 2)

        ws.sendMessage(CompanionMessage.GetState(nextId()))

        coroutineScope {
            val reader = launch { readLoop(ws) }
            val pinger = launch { pingLoop(ws) }
            reader.join()
            pinger.cancel()
        }
    }

    private suspend fun readLoop(ws: DefaultClientWebSocketSession) {
        while (true) {
            val decoded = ws.receiveMessage() ?: return
            val msg = (decoded as? DecodedMessage.Known)?.message ?: continue
            when (msg) {
                is CompanionMessage.State -> {
                    var s = msg.toPlayerState()
                    if (s.playback.updatedAt <= 0) {
                        s = s.copy(playback = s.playback.copy(updatedAt = System.currentTimeMillis()))
                    }
                    _state.value = s
                    if (_connection.value !is ConnectionState.Connected) {
                        _connection.value = ConnectionState.Connected(hostName, 2)
                    }
                }
                is CompanionMessage.Pong -> pongDeferred?.complete(Unit)
                is CompanionMessage.Ping -> ws.sendMessage(CompanionMessage.Pong())
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

    private suspend fun pingLoop(ws: DefaultClientWebSocketSession) {
        while (true) {
            delay(pingIntervalMs)
            val deferred = CompletableDeferred<Unit>()
            pongDeferred = deferred
            ws.sendMessage(CompanionMessage.Ping())
            val ok = withTimeoutOrNull(pongTimeoutMs) { deferred.await() } != null
            if (!ok) {
                Log.d(TAG, "no pong in ${pongTimeoutMs} ms, reconnecting")
                _connection.value = ConnectionState.Degraded(hostName, 1)
                ws.closeQuietly() // ends readLoop → runSession returns → reconnect
                return
            }
        }
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
        val ws = session ?: throw IllegalStateException("no session")
        val id = nextId()
        val deferred = CompletableDeferred<CompanionMessage>()
        pending[id] = deferred
        try {
            ws.sendMessage(build(id))
            val reply = withTimeout(requestTimeoutMs) { deferred.await() }
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
        if (c is ConnectionState.Connected || c is ConnectionState.Degraded) return session != null
        if (c is ConnectionState.Failed || c is ConnectionState.Disconnected) return false
        return withTimeoutOrNull(commandBufferMs) {
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
    override suspend fun listDisplays(): Result<List<dev.vlcremote.protocol.companion.Display>> = runCatching {
        val reply = request { id -> CommandMapping.toSys(id, SystemCommand.ListDisplays) }
        (reply as? CompanionMessage.Displays)?.items ?: emptyList()
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

    /** Bearer header for the companion's `/art` proxy. */
    fun authorizationHeader(): String? = credentials.token?.let { "Bearer $it" }

    private companion object {
        const val TAG = "CompanionBackend"
    }
}
