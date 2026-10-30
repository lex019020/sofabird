package app.sofabird.data.discovery

import app.sofabird.protocol.companion.COMPANION_WS_PORT
import app.sofabird.protocol.model.VlcProcessStatus

enum class DiscoverySource { NSD, UDP_BEACON }

/** A PC with the companion seen on the network by any discovery method, merged by host address. */
data class DiscoveredHost(
    val host: String,
    val name: String,
    val wsPort: Int = COMPANION_WS_PORT,
    /** The encrypted WebSocket's port (beacon / `/info` `tls.port`); `null` = not said. */
    val tlsPort: Int? = null,
    /** Companion `/info`: whether pairing is needed; `null` when unknown. */
    val needsPairing: Boolean? = null,
    /** Companion `/info` → `vlc.status`; `null` = unknown. */
    val vlcStatus: VlcProcessStatus? = null,
    /** When `/info` was last read (0 = never). */
    val infoAt: Long = 0L,
    val pcId: String? = null,
    /** Companion's Bluetooth adapter address (beacon / `/info` `bt`), normalized. */
    val bt: String? = null,
    val sources: Set<DiscoverySource> = emptySet(),
    val lastSeen: Long = System.currentTimeMillis(),
) {
    val displayName: String get() = name.ifBlank { host }

    fun merge(other: DiscoveredHost): DiscoveredHost = copy(
        name = other.name.ifBlank { name },
        wsPort = other.wsPort,
        tlsPort = other.tlsPort ?: tlsPort,
        needsPairing = other.needsPairing ?: needsPairing,
        vlcStatus = other.vlcStatus ?: vlcStatus,
        infoAt = maxOf(infoAt, other.infoAt),
        pcId = other.pcId ?: pcId,
        bt = other.bt ?: bt,
        sources = sources + other.sources,
        lastSeen = maxOf(lastSeen, other.lastSeen),
    )
}
