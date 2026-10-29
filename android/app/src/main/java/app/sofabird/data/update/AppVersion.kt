package app.sofabird.data.update

/**
 * A SemVer version: "0.4.0", "0.4.0-beta0064" (a test build), "v0.4.0" (a release tag). A release is newer than its
 * test builds; test builds are ordered by their number (zero-padded, so the text order works). Build metadata after
 * "+" is ignored.
 */
class AppVersion private constructor(
    private val core: List<Int>,
    val preRelease: List<String>,
    private val text: String,
) : Comparable<AppVersion> {

    val isPreRelease: Boolean get() = preRelease.isNotEmpty()

    override fun compareTo(other: AppVersion): Int {
        for (i in core.indices) core[i].compareTo(other.core[i]).let { if (it != 0) return it }
        // Without a pre-release part is newer: 0.4.0 > 0.4.0-beta0064.
        if (preRelease.isEmpty() || other.preRelease.isEmpty()) return other.preRelease.size.compareTo(preRelease.size)
        for (i in 0 until minOf(preRelease.size, other.preRelease.size)) {
            comparePart(preRelease[i], other.preRelease[i]).let { if (it != 0) return it }
        }
        return preRelease.size.compareTo(other.preRelease.size)
    }

    override fun equals(other: Any?): Boolean = other is AppVersion && compareTo(other) == 0
    override fun hashCode(): Int = core.hashCode() * 31 + preRelease.hashCode()
    override fun toString(): String = text

    companion object {
        private const val CORE_PARTS = 3 // MAJOR.MINOR.PATCH

        fun parse(value: String): AppVersion? {
            val text = value.trim().removePrefix("v").substringBefore('+')
            val coreText = text.substringBefore('-')
            val core = coreText.split('.').map { it.toIntOrNull() ?: return null }
            if (core.size != CORE_PARTS) return null
            val pre = if ('-' in text) text.substringAfter('-').split('.') else emptyList()
            if (pre.any { it.isEmpty() }) return null
            return AppVersion(core, pre, text)
        }

        /** SemVer: numbers by value and below words, words by text. */
        private fun comparePart(a: String, b: String): Int {
            val x = a.toLongOrNull()
            val y = b.toLongOrNull()
            return when {
                x != null && y != null -> x.compareTo(y)
                x != null -> -1
                y != null -> 1
                else -> a.compareTo(b)
            }
        }
    }
}

/** Which builds the app updates to: releases only, or every test build (and the releases). */
enum class UpdateChannel {
    STABLE,
    TEST,
    ;

    companion object {
        /** A test build follows test builds, a release follows releases. */
        fun of(installed: AppVersion?): UpdateChannel = if (installed?.isPreRelease == true) TEST else STABLE
    }
}
