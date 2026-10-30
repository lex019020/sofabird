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
 * A connection to the Windows companion, which controls VLC on the PC. The implementation lives in the app
 * module; this interface is pure Kotlin.
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

    suspend fun sendSystem(command: SystemCommand): Result<Unit>

    /**
     * `input` (needs the `input` capability): written on the current link at once, without a reply; dropped while
     * there is no link. Called from one coroutine, so the order is kept.
     */
    suspend fun sendInput(input: PcInput) {}

    /** List [uri] (a `file:///…/` directory; `file:///` = drives). `..` is never included. */
    suspend fun browse(uri: String): Result<List<Entry>>

    suspend fun playlist(): Result<List<PlaylistItem>>

    /** `getAudioDevices` (needs `audioDevices` capability). */
    suspend fun audioDevices(): Result<List<AudioDevice>>

    /** `sys listDisplays` (needs `windowControl` capability). */
    suspend fun listDisplays(): Result<DisplayList>

    /** `getHistory` (needs `history` capability). */
    suspend fun history(filter: HistoryFilter, limit: Int, offset: Int = 0): Result<HistoryPage>

    /** `historyUpdate`: mark watched / unwatched or remove one entry. */
    suspend fun updateHistory(uri: String, change: HistoryChange): Result<Unit>

    /** `historyClear`. */
    suspend fun clearHistory(): Result<Unit>

    /** `getLibrary` (needs `library` capability). */
    suspend fun library(): Result<Library>

    /** `getShow`: one series with its episodes. */
    suspend fun show(key: String): Result<ShowDetails>

    /** Ask the backend to re-fetch state now (e.g. after returning to the foreground). */
    suspend fun refresh()

    /** Whether the backend may poll right now (app visible or a foreground service is running). */
    fun setActive(active: Boolean)
}
