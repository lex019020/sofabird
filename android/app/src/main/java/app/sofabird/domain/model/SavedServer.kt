package app.sofabird.domain.model

import app.sofabird.protocol.companion.BtAddress
import app.sofabird.protocol.companion.COMPANION_WS_PORT
import kotlinx.serialization.Serializable

@Serializable
enum class ServerMode { DIRECT, COMPANION }

/** A PC the user connected to at least once. Secrets (password/token) live in [app.sofabird.data.secrets.SecretStore]. */
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
    /** Companion PC's network adapters (`welcome.macs`): "Wake up the PC" when it does not answer. */
    val wakeMacs: List<String> = emptyList(),
    /** The companion's encrypted WebSocket port (`tls.port`), once used. */
    val tlsPort: Int? = null,
    /** SHA-256 of the companion's certificate, pinned at the first encrypted connection: from then on only that. */
    val certPin: String? = null,
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

    // A different installation GUID means another PC, whatever the address says.
    fun samePc(s: SavedServer) = s.pcId == null || pcId == null || s.pcId == pcId
    return serverId?.let { id -> companions.firstOrNull { it.id == id } }
        ?: pcId?.let { pc -> companions.firstOrNull { it.pcId == pc } }
        ?: btAddress.takeIf { BtAddress.normalize(it) != null }
            ?.let { bt -> companions.firstOrNull { BtAddress.same(it.btAddress, bt) && samePc(it) } }
        ?: host.takeIf { it.isNotBlank() }
            ?.let { h -> companions.firstOrNull { it.host.equals(h, ignoreCase = true) && it.wsPort == wsPort && samePc(it) } }
}

/** A companion seen on the LAN (beacon, NSD + `/info`) or greeting us (`welcome`). */
data class CompanionSighting(
    val pcId: String?,
    val host: String? = null,
    val wsPort: Int? = null,
    val btAddress: String? = null,
    /** `welcome.macs`; `null` — not said. */
    val macs: List<String>? = null,
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
 * Never clears anything (a beacon without `bt`, radio off on the PC, keeps the known address) but `macs: []`.
 */
fun SavedServer.mergeSighting(sighting: CompanionSighting): SavedServer = copy(
    host = sighting.host?.takeIf { it.isNotBlank() } ?: host,
    wsPort = sighting.wsPort?.takeIf { sighting.host?.isNotBlank() == true } ?: wsPort,
    btAddress = BtAddress.normalize(sighting.btAddress) ?: btAddress,
    pcId = pcId ?: sighting.pcId,
    // The one exception: a companion saying `macs: []` means the PC cannot be woken (no "Wake up the PC" then).
    wakeMacs = sighting.macs ?: wakeMacs,
)
