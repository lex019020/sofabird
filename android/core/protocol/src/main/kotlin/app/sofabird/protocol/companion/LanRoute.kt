package app.sofabird.protocol.companion

/**
 * How to open the LAN link to a companion: the encrypted WebSocket (`wss`) on [port], trusting only [pin] — or, without
 * a pin yet (trust on first use), the certificate it shows, which is pinned once the companion accepted the phone.
 * There is no plain WebSocket (since 0.5 the companion answers a plain `/ws` with `426`).
 */
data class LanRoute(val port: Int, val pin: String? = null)

/** The route to a companion: its known `tls.port` (from `welcome`, the beacon or `/info`), else [COMPANION_TLS_PORT]. */
fun lanRoute(tlsPort: Int?, pin: String?): LanRoute = LanRoute(tlsPort ?: COMPANION_TLS_PORT, pin)

/**
 * The companion's artwork (`https://<its LAN address>:<tls port>/art?…` in `state.item.artUrl`) at [host], the address
 * the link was opened to: the pinned certificate and the token for it are kept by that host and [tlsPort], and the
 * companion may name another address of the PC. Other URLs (TMDB, YouTube thumbnails) are left unchanged.
 */
fun artViaLink(url: String?, host: String, tlsPort: Int): String? {
    val match = url?.let { COMPANION_ART.matchEntire(it) } ?: return url
    if (match.groupValues[1].toIntOrNull() != tlsPort) return url
    val authority = if (':' in host && !host.startsWith("[")) "[$host]" else host
    return "https://$authority:$tlsPort${match.groupValues[2]}"
}

/** `https://<host>:<port>/art[?…]`: groups — the port, the path with the query. */
private val COMPANION_ART = Regex("""https://[^/]*:(\d+)(/art(?:\?.*)?)""", RegexOption.IGNORE_CASE)
