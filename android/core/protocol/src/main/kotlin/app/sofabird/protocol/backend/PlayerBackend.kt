package app.sofabird.protocol.backend

import app.sofabird.protocol.companion.AudioDevice
import app.sofabird.protocol.companion.DisplayList
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.HistoryChange
import app.sofabird.protocol.model.HistoryFilter
import app.sofabird.protocol.model.HistoryPage
import app.sofabird.protocol.model.Library
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlaylistItem
import app.sofabird.protocol.model.ShowDetails
import kotlinx.coroutines.flow.StateFlow

/**
 * A connection to a player: direct VLC HTTP (levels 0/1) or the Windows companion (level 2).
 * Implementations live in the app module; this interface is pure Kotlin.
 */
interface PlayerBackend {
    /** What the current connection can do. */
    val capabilities: StateFlow<Capabilities>

    /** Latest known player state (raw, not interpolated). */
    val state: StateFlow<PlayerState>

    val connection: StateFlow<ConnectionState>

    /** Open the connection and start receiving state. Idempotent. */
    suspend fun connect()

    /** Close the connection and stop all background work. */
    suspend fun disconnect()

    suspend fun send(command: PlayerCommand): Result<Unit>

    suspend fun sendSystem(command: SystemCommand): Result<Unit> =
        Result.failure(UnsupportedOperationException("system commands need the companion"))

    /** List [uri] (a `file:///…/` directory; `file:///` = drives). `..` is never included. */
    suspend fun browse(uri: String): Result<List<Entry>>

    suspend fun playlist(): Result<List<PlaylistItem>>

    /** Companion `getAudioDevices` (needs `audioDevices` capability). */
    suspend fun audioDevices(): Result<List<AudioDevice>> =
        Result.failure(UnsupportedOperationException("audio devices need the companion"))

    /** Companion `sys listDisplays` (needs `windowControl` capability). */
    suspend fun listDisplays(): Result<DisplayList> =
        Result.failure(UnsupportedOperationException("displays need the companion"))

    /** Companion `getHistory` (needs `history` capability). */
    suspend fun history(filter: HistoryFilter, limit: Int, offset: Int = 0): Result<HistoryPage> =
        Result.failure(UnsupportedOperationException("watch history needs the companion"))

    /** Companion `historyUpdate`: mark watched / unwatched or remove one entry. */
    suspend fun updateHistory(uri: String, change: HistoryChange): Result<Unit> =
        Result.failure(UnsupportedOperationException("watch history needs the companion"))

    /** Companion `historyClear`. */
    suspend fun clearHistory(): Result<Unit> =
        Result.failure(UnsupportedOperationException("watch history needs the companion"))

    /** Companion `getLibrary` (needs `library` capability). */
    suspend fun library(): Result<Library> =
        Result.failure(UnsupportedOperationException("the library needs the companion"))

    /** Companion `getShow`: one series with its episodes. */
    suspend fun show(key: String): Result<ShowDetails> =
        Result.failure(UnsupportedOperationException("the library needs the companion"))

    /** Ask the backend to re-fetch state now (e.g. after returning to the foreground). */
    suspend fun refresh()

    /** Whether the backend may poll right now (app visible or a foreground service is running). */
    fun setActive(active: Boolean)
}
