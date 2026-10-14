package app.sofabird.ui.common

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

/** File size in the phone's language (system formatter: "1.5 GB"; units and decimal separator follow the locale). */
fun formatSize(context: android.content.Context, bytes: Long?): String =
    if (bytes == null || bytes < 0) "" else android.text.format.Formatter.formatShortFileSize(context, bytes)

fun formatRate(rate: Double): String {
    val r = Math.round(rate * 100) / 100.0
    return if (r == Math.floor(r)) "${r.toInt()}×" else String.format(Locale.ROOT, "%.2f×", r).trimEnd('0').trimEnd('.') + ""
}

/** Delay in milliseconds with a sign; [msUnit] is the localized unit (R.string.unit_ms). */
fun formatDelay(seconds: Double, msUnit: String): String {
    val ms = Math.round(seconds * 1000)
    return if (ms == 0L) "0 $msUnit" else String.format(Locale.ROOT, "%+d %s", ms, msUnit)
}

/** Percent-decoded last path segment of a file URI for display; a drive root reads as `D:`. */
fun displayNameFromUri(uri: String): String {
    val trimmed = uri.trimEnd('/')
    val last = trimmed.substringAfterLast('/')
    // URLDecoder is a form decoder: a literal "+" would turn into a space.
    val name = runCatching { java.net.URLDecoder.decode(last.replace("+", "%2B"), "UTF-8") }.getOrDefault(last)
    return if (DRIVE_ROOT.matches(name)) name.uppercase() else name
}

private val DRIVE_ROOT = Regex("^[A-Za-z]:$")
