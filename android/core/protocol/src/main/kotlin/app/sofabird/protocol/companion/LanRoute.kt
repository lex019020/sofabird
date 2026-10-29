package app.sofabird.protocol.companion

/**
 * One way to open the LAN link to a companion: the encrypted WebSocket on [port] trusting only [pin] (or, without a
 * pin yet, the certificate it shows, which is then pinned), or the plain one ([tls] = false).
 */
data class LanRoute(val port: Int, val tls: Boolean, val pin: String? = null)

/**
 * The routes to try, in order. A pinned PC is reached only encrypted, never in plain text again. A PC known to have
 * the encrypted WebSocket ([tlsPort], from its `welcome` / `/info`) is tried encrypted first (trust on first use),
 * then plain; one not known to have it (an older companion, or not seen yet) plainly — its `welcome` then tells.
 */
fun lanRoutes(wsPort: Int, tlsPort: Int?, pin: String?): List<LanRoute> = when {
    pin != null -> listOf(LanRoute(tlsPort ?: COMPANION_TLS_PORT, tls = true, pin = pin))
    tlsPort != null -> listOf(LanRoute(tlsPort, tls = true), LanRoute(wsPort, tls = false))
    else -> listOf(LanRoute(wsPort, tls = false))
}

/**
 * The companion's artwork (`http://<pc>:<ws port>/art?…` in `state`) through the encrypted listener of the link in
 * use, so that the token in its `Authorization` never goes out in plain text; other URLs unchanged.
 */
fun artOverTls(url: String?, host: String, wsPort: Int, tlsPort: Int): String? {
    if (url == null) return null
    val prefix = "http://"
    if (!url.startsWith(prefix)) return url
    val rest = url.substring(prefix.length)
    val slash = rest.indexOf('/')
    if (slash < 0 || !rest.substring(slash).startsWith("/art")) return url
    val authority = rest.substring(0, slash)
    if (authority.substringAfterLast(':', "").toIntOrNull() != wsPort) return url
    return "https://$host:$tlsPort${rest.substring(slash)}"
}
