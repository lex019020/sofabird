package dev.vlcremote.protocol.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
enum class EntryType {
    @SerialName("dir")
    DIR,

    @SerialName("file")
    FILE,

    @SerialName("unknown")
    UNKNOWN,
}

/** One item of a directory listing (`browse.json` element or companion `entries.items[]`). */
@Serializable
data class Entry(
    val name: String,
    val uri: String,
    val type: EntryType,
    val size: Long? = null,
    /** Unix seconds. */
    val modified: Long? = null,
    /** Companion with `history`: watched fraction 0..1 of a file that is in the history. */
    val progress: Double? = null,
    /** Companion with `history`: the file was watched to the end (or marked watched). */
    val finished: Boolean? = null,
) {
    val isDir: Boolean get() = type == EntryType.DIR

    val extension: String
        get() = name.substringAfterLast('.', "").lowercase()

    val isMediaFile: Boolean
        get() = !isDir && extension in MEDIA_EXTENSIONS

    companion object {
        val MEDIA_EXTENSIONS: Set<String> = setOf(
            "mkv", "mp4", "m4v", "avi", "mov", "wmv", "ts", "m2ts", "mts", "mpg", "mpeg", "webm",
            "flv", "vob", "3gp", "ogv", "iso",
            "mp3", "flac", "aac", "m4a", "ogg", "opus", "wav", "wma", "ape", "dts", "ac3",
            "srt", "ass", "ssa", "sub", "vtt",
        )
        val SUBTITLE_EXTENSIONS: Set<String> = setOf("srt", "ass", "ssa", "sub", "vtt")
    }
}

/** One playlist entry (leaf of `playlist.json` or companion `playlist.items[]`). */
@Serializable
data class PlaylistItem(
    val plId: Int,
    val name: String,
    val uri: String,
    val durationSec: Long = 0,
    val current: Boolean = false,
)
