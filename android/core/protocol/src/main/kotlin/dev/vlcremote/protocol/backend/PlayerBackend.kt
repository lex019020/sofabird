package dev.vlcremote.protocol.backend

import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlaylistItem
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

    /** Ask the backend to re-fetch state now (e.g. after returning to the foreground). */
    suspend fun refresh()

    /** Whether the backend may poll right now (app visible or a foreground service is running). */
    fun setActive(active: Boolean)
}
