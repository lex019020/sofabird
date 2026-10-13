package dev.vlcremote.app.data.servers

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import dev.vlcremote.app.domain.model.CompanionSighting
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.matchSighting
import dev.vlcremote.app.domain.model.mergeSighting
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json

private val Context.serversStore: DataStore<Preferences> by preferencesDataStore(name = "servers")

/** Saved PCs (no secrets). Stored as one JSON list in DataStore — a handful of entries at most. */
class ServerRepository(context: Context, scope: CoroutineScope) {
    private val store = context.applicationContext.serversStore
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val serializer = ListSerializer(SavedServer.serializer())

    private val keyServers = stringPreferencesKey("servers_json")
    private val keyLastId = stringPreferencesKey("last_server_id")

    val servers: Flow<List<SavedServer>> = store.data.map { p -> decode(p[keyServers]) }
    val serversState: StateFlow<List<SavedServer>> = servers.stateIn(scope, SharingStarted.Eagerly, emptyList())

    val lastServerId: Flow<String?> = store.data.map { it[keyLastId] }

    suspend fun lastServer(): SavedServer? {
        val p = store.data.first()
        val id = p[keyLastId] ?: return null
        return decode(p[keyServers]).firstOrNull { it.id == id }
    }

    suspend fun get(id: String): SavedServer? = servers.first().firstOrNull { it.id == id }

    /** Inserts or replaces by id, and marks it as last used. */
    suspend fun upsert(server: SavedServer, markLastUsed: Boolean = true) {
        store.edit { p ->
            val list = decode(p[keyServers]).filterNot { it.id == server.id } + server
            p[keyServers] = json.encodeToString(serializer, list.sortedByDescending { it.lastUsedAt })
            if (markLastUsed) p[keyLastId] = server.id
        }
    }

    suspend fun update(id: String, block: (SavedServer) -> SavedServer) {
        store.edit { p ->
            val list = decode(p[keyServers])
            val updated = list.map { if (it.id == id) block(it) else it }
            p[keyServers] = json.encodeToString(serializer, updated)
        }
    }

    suspend fun remove(id: String) {
        store.edit { p ->
            p[keyServers] = json.encodeToString(serializer, decode(p[keyServers]).filterNot { it.id == id })
            if (p[keyLastId] == id) p.remove(keyLastId)
        }
    }

    /**
     * Merges a companion sighting (LAN beacon / `/info`) into the saved companion with the same GUID
     * or Bluetooth address. Returns the updated entry, or `null` when nothing matched or changed.
     */
    suspend fun applySighting(sighting: CompanionSighting): SavedServer? {
        var updated: SavedServer? = null
        store.edit { p ->
            val list = decode(p[keyServers])
            val match = list.matchSighting(sighting) ?: return@edit
            val merged = match.mergeSighting(sighting)
            if (merged == match) return@edit
            updated = merged
            p[keyServers] = json.encodeToString(serializer, list.map { if (it.id == match.id) merged else it })
        }
        return updated
    }

    suspend fun markLastUsed(id: String) {
        store.edit { p -> p[keyLastId] = id }
        update(id) { it.copy(lastUsedAt = System.currentTimeMillis()) }
    }

    private fun decode(raw: String?): List<SavedServer> =
        raw?.let { runCatching { json.decodeFromString(serializer, it) }.getOrNull() } ?: emptyList()
}
