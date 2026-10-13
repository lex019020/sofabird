package dev.vlcremote.protocol.companion

/** RFCOMM service of the companion (protocol/README.md, "Transport"). Found by SDP lookup, no fixed channel. */
const val COMPANION_RFCOMM_UUID = "47fe5bd6-98ad-4d9f-9812-174b3394037d"

/** Longest NDJSON line accepted over RFCOMM, in bytes (without the `\n`). */
const val NDJSON_MAX_LINE_BYTES = 1 shl 20

/** Bluetooth device addresses as the protocol carries them: `AA:BB:CC:DD:EE:FF`, upper case. */
object BtAddress {
    private val PATTERN = Regex("^([0-9A-F]{2}:){5}[0-9A-F]{2}$")

    /**
     * Canonical form of [raw] (`aa-bb-…` and lower case accepted), or `null` when it is missing,
     * malformed or the all-zero placeholder some stacks report.
     */
    fun normalize(raw: String?): String? {
        val candidate = raw?.trim()?.uppercase()?.replace('-', ':') ?: return null
        if (!PATTERN.matches(candidate) || candidate == "00:00:00:00:00:00") return null
        return candidate
    }

    fun same(a: String?, b: String?): Boolean {
        val na = normalize(a) ?: return false
        return na == normalize(b)
    }
}
