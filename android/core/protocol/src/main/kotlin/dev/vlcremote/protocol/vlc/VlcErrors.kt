package dev.vlcremote.protocol.vlc

/** VLC answered 200 with an HTML page `<title>Error loading …` — a Lua script failed. */
class VlcLuaErrorException(val detail: String?) :
    Exception("VLC Lua error" + (detail?.let { ": $it" } ?: ""))

/** Body was not the JSON we expected. */
class VlcParseException(message: String, cause: Throwable? = null) : Exception(message, cause)

object VlcErrorPage {
    private val titleRegex = Regex("<title>\\s*Error loading([^<]*)</title>", RegexOption.IGNORE_CASE)

    fun isErrorPage(body: String): Boolean {
        val head = body.trimStart()
        if (head.startsWith("{") || head.startsWith("[")) return false
        return titleRegex.containsMatchIn(body)
    }

    fun extractDetail(body: String): String? {
        val m = titleRegex.find(body) ?: return null
        val detail = m.groupValues[1].trim()
        return detail.ifEmpty { null }
    }

    /** Throws [VlcLuaErrorException] when [body] is an error page. */
    fun check(body: String) {
        if (isErrorPage(body)) throw VlcLuaErrorException(extractDetail(body))
    }
}
