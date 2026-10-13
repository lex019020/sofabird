package dev.vlcremote.app.domain.model

import dev.vlcremote.protocol.companion.COMPANION_WS_PORT
import kotlinx.serialization.Serializable

@Serializable
enum class ServerMode { DIRECT, COMPANION }

/** A PC the user connected to at least once. Secrets (password/token) live in [dev.vlcremote.app.data.secrets.SecretStore]. */
@Serializable
data class SavedServer(
    val id: String,
    val name: String,
    val host: String,
    val httpPort: Int = DEFAULT_HTTP_PORT,
    val wsPort: Int = COMPANION_WS_PORT,
    val mode: ServerMode = ServerMode.DIRECT,
    val lastUsedAt: Long = 0L,
    /** Last folder opened in the Library on this server. */
    val lastBrowseUri: String? = null,
    val favoriteFolders: List<String> = emptyList(),
    /** Companion's stable installation GUID (when known). */
    val pcId: String? = null,
) {
    val displayName: String get() = name.ifBlank { host }

    val isCompanion: Boolean get() = mode == ServerMode.COMPANION

    companion object {
        const val DEFAULT_HTTP_PORT = 8080
    }
}

/**
 * The saved companion server that a discovered/typed companion corresponds to: by saved id, then by
 * the companion's installation GUID, then by address (unless the GUIDs say it is another PC).
 */
fun List<SavedServer>.findCompanion(
    host: String,
    wsPort: Int,
    pcId: String?,
    serverId: String? = null,
): SavedServer? {
    val companions = filter { it.isCompanion }
    serverId?.let { id -> companions.firstOrNull { it.id == id }?.let { return it } }
    pcId?.let { pc -> companions.firstOrNull { it.pcId == pc }?.let { return it } }
    return companions.firstOrNull {
        it.host.equals(host, ignoreCase = true) && it.wsPort == wsPort && (it.pcId == null || pcId == null || it.pcId == pcId)
    }
}
