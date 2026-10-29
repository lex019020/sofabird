package app.sofabird.protocol.companion

/**
 * Wake-on-LAN: the "magic packet" that wakes a sleeping PC whose network adapter listens for it — 6 bytes of `0xFF`
 * and the adapter's MAC 16 times, sent as a UDP broadcast (the PC has no address while it sleeps).
 */
object MagicPacket {
    /** The ports wake-on-LAN tools use ("discard" and "echo"); the adapter does not care. */
    val PORTS = listOf(9, 7)

    private const val MAC_BYTES = 6
    private const val REPEAT = 16
    private const val HEX = 16

    /** The packet for [mac] (`AA:BB:CC:DD:EE:FF`, `AA-BB-…` or bare hex); `null` when it is not a MAC. */
    fun of(mac: String): ByteArray? {
        val hex = mac.filter { it.isLetterOrDigit() }
        if (hex.length != MAC_BYTES * 2 || mac.any { it.isLetterOrDigit() && it.digitToIntOrNull(HEX) == null }) return null
        val address = ByteArray(MAC_BYTES) { i -> hex.substring(i * 2, i * 2 + 2).toInt(HEX).toByte() }
        return ByteArray(MAC_BYTES) { 0xFF.toByte() } + ByteArray(MAC_BYTES * REPEAT) { i -> address[i % MAC_BYTES] }
    }
}
