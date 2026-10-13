package dev.vlcremote.app.domain.model

import dev.vlcremote.protocol.companion.BtAddress
import dev.vlcremote.protocol.companion.COMPANION_WS_PORT
import kotlinx.serialization.Serializable

@Serializable
enum class ServerMode { DIRECT, COMPANION }

/** A PC the user connected to at least once. Secrets (password/token) live in [dev.vlcremote.app.data.secrets.SecretStore]. */
@Serializable
data class SavedServer(
    val id: String,
    val name: String,
    /** LAN address; empty for a companion paired over Bluetooth until it is seen on the network. */
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
    /** Companion PC's Bluetooth address (`welcome`/beacon/`/info` `bt`): RFCOMM fallback when the LAN is unavailable. */
    val btAddress: String? = null,
) {
    val displayName: String get() = name.ifBlank { host.ifBlank { btAddress.orEmpty() } }

    val isCompanion: Boolean get() = mode == ServerMode.COMPANION

    val hasLanAddress: Boolean get() = host.isNotBlank()

    companion object {
        const val DEFAULT_HTTP_PORT = 8080
    }
}

/**
 * The saved companion server that a discovered/typed companion corresponds to: by saved id, then by
 * the companion's installation GUID, then by Bluetooth address, then by LAN address (unless the
 * GUIDs say it is another PC).
 */
fun List<SavedServer>.findCompanion(
    host: String,
    wsPort: Int,
    pcId: String?,
    serverId: String? = null,
    btAddress: String? = null,
): SavedServer? {
    val companions = filter { it.isCompanion }
    serverId?.let { id -> companions.firstOrNull { it.id == id }?.let { return it } }
    pcId?.let { pc -> companions.firstOrNull { it.pcId == pc }?.let { return it } }
    if (BtAddress.normalize(btAddress) != null) {
        companions.firstOrNull { BtAddress.same(it.btAddress, btAddress) && (it.pcId == null || pcId == null || it.pcId == pcId) }
            ?.let { return it }
    }
    if (host.isBlank()) return null
    return companions.firstOrNull {
        it.host.equals(host, ignoreCase = true) && it.wsPort == wsPort && (it.pcId == null || pcId == null || it.pcId == pcId)
    }
}

/** A companion seen on the LAN (beacon, NSD + `/info`) or greeting us (`welcome`). */
data class CompanionSighting(
    val pcId: String?,
    val host: String? = null,
    val wsPort: Int? = null,
    val btAddress: String? = null,
)

/**
 * The saved companion [sighting] refers to: only by installation GUID or Bluetooth address — an
 * address alone is not proof (DHCP hands it to another PC).
 */
fun List<SavedServer>.matchSighting(sighting: CompanionSighting): SavedServer? {
    val companions = filter { it.isCompanion }
    sighting.pcId?.let { pc -> companions.firstOrNull { it.pcId == pc }?.let { return it } }
    val bt = BtAddress.normalize(sighting.btAddress) ?: return null
    return companions.firstOrNull { BtAddress.same(it.btAddress, bt) && (it.pcId == null || sighting.pcId == null) }
}

/**
 * Merges what was seen about this companion into the saved entry: a LAN address for a PC paired
 * over Bluetooth (or one that moved), the Bluetooth address for a PC paired over Wi-Fi, the GUID.
 * Never clears anything: a beacon without `bt` (radio off on the PC) keeps the known address.
 */
fun SavedServer.mergeSighting(sighting: CompanionSighting): SavedServer = copy(
    host = sighting.host?.takeIf { it.isNotBlank() } ?: host,
    wsPort = sighting.wsPort?.takeIf { sighting.host?.isNotBlank() == true } ?: wsPort,
    btAddress = BtAddress.normalize(sighting.btAddress) ?: btAddress,
    pcId = pcId ?: sighting.pcId,
)
