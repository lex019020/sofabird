package dev.vlcremote.app.data.discovery

import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.protocol.companion.COMPANION_WS_PORT
import dev.vlcremote.protocol.model.VlcProcessStatus

enum class DiscoverySource { NSD, UDP_BEACON, SUBNET_SWEEP }

/** A PC seen on the network by any discovery method, merged by host address. */
data class DiscoveredHost(
    val host: String,
    val name: String,
    /** Companion present (WebSocket) — otherwise only VLC's HTTP interface answered. */
    val hasCompanion: Boolean,
    val wsPort: Int = COMPANION_WS_PORT,
    val httpPort: Int = SavedServer.DEFAULT_HTTP_PORT,
    /** Companion `/info`: whether pairing is needed; `null` when unknown. */
    val needsPairing: Boolean? = null,
    /** VLC running (companion `/info`) or VLC answered on 8080 (sweep). `null` = unknown. */
    val vlcRunning: Boolean? = null,
    val vlcVersion: String? = null,
    /** Companion `/info` → `vlc.status` (derived from `running` for old companions); `null` = unknown. */
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
        name = if (other.name.isNotBlank() && (name.isBlank() || other.hasCompanion)) other.name else name,
        hasCompanion = hasCompanion || other.hasCompanion,
        wsPort = if (other.hasCompanion) other.wsPort else wsPort,
        httpPort = other.httpPort.takeIf { DiscoverySource.SUBNET_SWEEP in other.sources } ?: httpPort,
        needsPairing = other.needsPairing ?: needsPairing,
        vlcRunning = other.vlcRunning ?: vlcRunning,
        vlcVersion = other.vlcVersion ?: vlcVersion,
        vlcStatus = other.vlcStatus ?: vlcStatus,
        infoAt = maxOf(infoAt, other.infoAt),
        pcId = other.pcId ?: pcId,
        bt = other.bt ?: bt,
        sources = sources + other.sources,
        lastSeen = maxOf(lastSeen, other.lastSeen),
    )
}
