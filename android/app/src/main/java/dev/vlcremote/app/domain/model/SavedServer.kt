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
