package dev.vlcremote.app.data.vlc

import android.util.Log
import dev.vlcremote.protocol.backend.PlayerBackend
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlaylistItem
import dev.vlcremote.protocol.vlc.RememberedTracks
import dev.vlcremote.protocol.vlc.VlcCommands
import dev.vlcremote.protocol.vlc.VlcStateMapper
import dev.vlcremote.protocol.vlc.VlcStatus
import dev.vlcremote.protocol.vlc.VlcStatusParser
import dev.vlcremote.protocol.vlc.VlcTracksPage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.onTimeout
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.cancellation.CancellationException
import kotlin.random.Random

/**
 * Direct mode (levels 0/1): polls `status.json` 1 s while playing, 3 s otherwise, only while
 * [setActive] is true. One request in flight per kind, sequence numbers to drop stale answers,
 * command responses applied immediately plus a "clean" status 500 ms later, mute emulated
 * client-side, current track remembered client-side on level 0 and reset on `currentplid` change.
 */
class VlcHttpBackend(
    private val api: VlcHttpApi,
    private val hostName: String,
    private val scope: CoroutineScope,
    private val clock: () -> Long = System::currentTimeMillis,
    private val pollPlayingMs: Long = 1_000,
    private val pollIdleMs: Long = 3_000,
    private val cleanStatusDelayMs: Long = 500,
    private val commandBufferMs: Long = 5_000,
) : PlayerBackend {

    private val _capabilities = MutableStateFlow(Capabilities.LEVEL0)
    override val capabilities: StateFlow<Capabilities> = _capabilities.asStateFlow()

    private val _state = MutableStateFlow(PlayerState.EMPTY)
    override val state: StateFlow<PlayerState> = _state.asStateFlow()

    private val _connection = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    override val connection: StateFlow<ConnectionState> = _connection.asStateFlow()

    /** 0 or 1; detected on every (re)connect. */
    @Volatile var level: Int = 0
        private set

    private val active = MutableStateFlow(true)
    private val wake = Channel<Unit>(capacity = 1, onBufferOverflow = BufferOverflow.DROP_LATEST)

    private val statusMutex = Mutex()
    private val playlistMutex = Mutex()
    private val browseMutex = Mutex()

    private val seq = AtomicLong(0)
    @Volatile private var lastAppliedSeq = 0L

    private var pollJob: Job? = null
    private var cleanJob: Job? = null

    private var remembered = RememberedTracks.NONE
    private var lastPlId: Int = Int.MIN_VALUE
    private var muted = false
    private var volumeBeforeMute: Int? = null
    private var failures = 0
    private var everConnected = false

    override suspend fun connect() {
        if (pollJob?.isActive == true) return
        failures = 0
        everConnected = false
        _connection.value = ConnectionState.Connecting(0)
        pollJob = scope.launch { pollLoop() }
    }

    override suspend fun disconnect() {
        cleanJob?.cancel()
        pollJob?.cancelAndJoin()
        pollJob = null
        _connection.value = ConnectionState.Disconnected
    }

    override fun setActive(active: Boolean) {
        this.active.value = active
        if (active) wake.trySend(Unit)
    }

    override suspend fun refresh() {
        wake.trySend(Unit)
    }

    // ------------------------------------------------------------------ polling

    private suspend fun pollLoop() {
        var attempt = 0
        while (scope.isActive) {
            active.first { it }
            val ok = pollOnce()
            if (connection.value is ConnectionState.Failed) return // auth error: wait for the user
            val delayMs = when {
                !ok -> backoff(attempt++)
                else -> {
                    attempt = 0
                    if (_state.value.isPlaying) pollPlayingMs else pollIdleMs
                }
            }
            // Sleep, but wake early on refresh()/setActive(true).
            select<Unit> {
                wake.onReceive { }
                onTimeout(delayMs) { }
            }
        }
    }

    private fun backoff(attempt: Int): Long {
        val base = (1_000L shl attempt.coerceAtMost(4)).coerceAtMost(16_000L)
        return base + Random.nextLong(0, 500)
    }

    /** One status poll (+ tracks page on level 1). Returns false on failure. */
    private suspend fun pollOnce(): Boolean {
        return try {
            if (!everConnected) detectLevel()
            statusMutex.withLock { fetchAndApply(null) }
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            onFailure(e)
            false
        }
    }

    private fun onFailure(e: Throwable) {
        val error = VlcErrorClassifier.fromThrowable(e)
        failures++
        Log.d(TAG, "poll failed ($failures): $error")
        _connection.value = when {
            VlcErrorClassifier.isAuthError(error) -> ConnectionState.Failed(error)
            everConnected && failures < DEGRADED_LIMIT -> ConnectionState.Degraded(hostName, failures)
            else -> ConnectionState.Connecting(failures, error)
        }
        if (failures >= DEGRADED_LIMIT) everConnected = false
    }

    private suspend fun detectLevel() {
        level = try {
            val page = VlcStatusParser.parseTracks(api.tracks().body)
            if (page.audio != null || page.chapter != null || page.video != null || page.spu != null) 1 else 0
        } catch (e: CancellationException) {
            throw e
        } catch (e: VlcHttpException) {
            if (e.statusCode == 404) 0 else if (VlcErrorClassifier.isAuthError(e.error)) throw e else 0
        } catch (e: Throwable) {
            0
        }
        _capabilities.value = if (level >= 1) Capabilities.LEVEL1 else Capabilities.LEVEL0
    }

    /**
     * Fetches `status.json` (optionally running [commandQuery]) and applies it unless a newer
     * response has already been applied. Must be called under [statusMutex].
     */
    private suspend fun fetchAndApply(commandQuery: String?): VlcStatus? {
        val mySeq = seq.incrementAndGet()
        val response = api.status(commandQuery)
        val status = VlcStatusParser.parseStatus(response.body)
        val page: VlcTracksPage? = if (level >= 1 && status.state != "stopped") {
            try {
                VlcStatusParser.parseTracks(api.tracks().body)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                null
            }
        } else {
            null
        }
        if (mySeq < lastAppliedSeq) return null // stale
        lastAppliedSeq = mySeq
        apply(status, page, response.latencyMs)
        return status
    }

    private fun apply(status: VlcStatus, page: VlcTracksPage?, latencyMs: Long) {
        if (status.currentPlId != lastPlId) {
            remembered = RememberedTracks.NONE
            lastPlId = status.currentPlId
        }
        if (muted && status.volume > 0) muted = false
        val previous = _state.value
        _state.value = VlcStateMapper.map(
            status = status,
            tracksPage = page,
            remembered = remembered,
            level = level,
            artUrl = api.artUrl(status.currentPlId.takeIf { it >= 0 }),
            muted = muted,
            nowMs = clock(),
            previous = previous,
        )
        failures = 0
        everConnected = true
        _connection.value = ConnectionState.Connected(hostName, level, latencyMs)
    }

    // ------------------------------------------------------------------ commands

    override suspend fun send(command: PlayerCommand): Result<Unit> {
        if (command is PlayerCommand.Launch || command is PlayerCommand.Resume || command == PlayerCommand.DismissNext) {
            return Result.failure(UnsupportedOperationException("${command::class.simpleName} needs the companion"))
        }
        if (!awaitOnline()) return Result.failure(VlcHttpException(ConnectionError.Timeout))
        return try {
            when (command) {
                is PlayerCommand.Mute -> mute(command.on)
                else -> {
                    if (command is PlayerCommand.SelectTrack) {
                        remembered = remembered.with(command.kind, command.id)
                    }
                    val s = _state.value
                    val toggles = VlcCommands.CurrentToggles(
                        fullscreen = s.video.fullscreen,
                        random = s.flags.random,
                        loop = s.flags.loop,
                        repeat = s.flags.repeat,
                        chapter = s.chapters.index.coerceAtLeast(0),
                    )
                    val queries = VlcCommands.forCommand(command, toggles)
                    for (q in queries) runCommand(q)
                    if (command is PlayerCommand.SelectTrack && level == 0) {
                        _state.value = _state.value.copy(
                            tracks = _state.value.tracks.withCurrent(command.kind, command.id),
                        )
                    }
                }
            }
            scheduleCleanStatus()
            Result.success(Unit)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            onFailure(e)
            Result.failure(e)
        }
    }

    private suspend fun mute(on: Boolean) {
        if (on) {
            val current = _state.value.volume.value
            if (current > 0) volumeBeforeMute = current
            runCommand(VlcCommands.volume(0))
            muted = true
        } else {
            muted = false
            runCommand(VlcCommands.volume(volumeBeforeMute ?: 256))
        }
        _state.value = _state.value.copy(volume = _state.value.volume.copy(muted = muted))
    }

    private suspend fun runCommand(query: String) {
        statusMutex.withLock { fetchAndApply(query) }
        wake.trySend(Unit) // re-arm the regular poll
    }

    private fun scheduleCleanStatus() {
        cleanJob?.cancel()
        cleanJob = scope.launch {
            delay(cleanStatusDelayMs)
            try {
                statusMutex.withLock { fetchAndApply(null) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                onFailure(e)
            }
        }
    }

    /** While degraded/reconnecting, hold the command up to [commandBufferMs]. */
    private suspend fun awaitOnline(): Boolean {
        val c = connection.value
        if (c is ConnectionState.Connected || c is ConnectionState.Degraded) return true
        if (c is ConnectionState.Failed || c is ConnectionState.Disconnected) return false
        wake.trySend(Unit)
        return withTimeoutOrNull(commandBufferMs) {
            connection.first { it is ConnectionState.Connected || it is ConnectionState.Failed }
        } is ConnectionState.Connected
    }

    // ------------------------------------------------------------------ data

    override suspend fun browse(uri: String): Result<List<Entry>> = browseMutex.withLock {
        runCatching { VlcStatusParser.parseBrowse(api.browse(uri).body) }
    }

    override suspend fun playlist(): Result<List<PlaylistItem>> = playlistMutex.withLock {
        runCatching { VlcStatusParser.parsePlaylist(api.playlist().body) }
    }

    private companion object {
        const val TAG = "VlcHttpBackend"
        const val DEGRADED_LIMIT = 3
    }
}
