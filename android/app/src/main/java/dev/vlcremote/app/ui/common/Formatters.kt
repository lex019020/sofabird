package dev.vlcremote.app.ui.common

import java.util.Locale

/** `h:mm:ss` or `m:ss`. */
fun formatTime(totalSeconds: Long): String {
    val s = totalSeconds.coerceAtLeast(0)
    val h = s / 3600
    val m = (s % 3600) / 60
    val sec = s % 60
    return if (h > 0) String.format(Locale.ROOT, "%d:%02d:%02d", h, m, sec) else String.format(Locale.ROOT, "%d:%02d", m, sec)
}

fun formatTime(seconds: Double): String = formatTime(seconds.toLong())

fun formatRemaining(position: Double, length: Long): String =
    if (length <= 0) "" else "-" + formatTime((length - position).coerceAtLeast(0.0))

fun formatSize(bytes: Long?): String {
    if (bytes == null || bytes < 0) return ""
    val units = arrayOf("Б", "КБ", "МБ", "ГБ", "ТБ")
    var v = bytes.toDouble()
    var i = 0
    while (v >= 1024 && i < units.lastIndex) {
        v /= 1024
        i++
    }
    return if (i == 0) "${bytes} ${units[0]}" else String.format(Locale.ROOT, "%.1f %s", v, units[i])
}

fun formatRate(rate: Double): String {
    val r = Math.round(rate * 100) / 100.0
    return if (r == Math.floor(r)) "${r.toInt()}×" else String.format(Locale.ROOT, "%.2f×", r).trimEnd('0').trimEnd('.') + ""
}

fun formatDelay(seconds: Double): String {
    val ms = Math.round(seconds * 1000)
    return if (ms == 0L) "0 мс" else String.format(Locale.ROOT, "%+d мс", ms)
}

/** Percent-decoded last path segment of a file URI for display. */
fun displayNameFromUri(uri: String): String {
    val trimmed = uri.trimEnd('/')
    val last = trimmed.substringAfterLast('/')
    return runCatching { java.net.URLDecoder.decode(last, "UTF-8") }.getOrDefault(last)
}
