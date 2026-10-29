package app.sofabird.protocol.model

import kotlinx.serialization.Serializable

/*
 * Watch history, resume prompt and next-episode offer (capability `history`, protocol/README.md
 * "Watch history and resume"). The companion is the source of truth; the phone only shows and edits it.
 */

/**
 * `state.resume`: a new input started at a remembered position and VLC waits paused for the answer
 * (`cmd resume`). The companion removes it once anyone answered (or Play was pressed in VLC).
 */
@Serializable
data class ResumePrompt(
    val uri: String = "",
    val title: String? = null,
    /** Seconds; where playback would continue. */
    val position: Long = 0,
    /** Seconds; 0 when unknown. */
    val length: Long = 0,
    val pending: Boolean = false,
) {
    /** Fraction watched, 0..1 (0 when the length is unknown). */
    val progress: Double
        get() = if (length > 0) (position.toDouble() / length).coerceIn(0.0, 1.0) else 0.0
}

/** Answer to the resume prompt (`cmd resume {"choice"}`). */
enum class ResumeChoice(val wire: String) {
    /** Seek to the remembered position and play. */
    RESUME("resume"),

    /** Play from 0. */
    RESTART("restart"),
}

/** `state.next`: the next unfinished episode of the folder, offered (never auto-played) after one ends. */
@Serializable
data class NextEpisode(
    val uri: String = "",
    val title: String? = null,
)

/** `state.history`: [revision] changes whenever the history changes, so lists can be refreshed. */
@Serializable
data class HistoryInfo(
    val revision: Long = 0,
)

/** `getHistory` filters. */
enum class HistoryFilter(val wire: String) {
    /** Started and not finished (≥ 5 min), plus the next unfinished episode of each series folder. */
    CONTINUE("continue"),

    /** Everything, newest first. */
    ALL("all"),

    /** The YouTube videos played on the TV only, newest first. */
    YOUTUBE("youtube"),
}

/** One element of `history.items`. */
@Serializable
data class HistoryItem(
    val uri: String,
    /** VLC's title (metadata or file name). */
    val title: String = "",
    /** File name. */
    val name: String? = null,
    /** Parent folder URI. */
    val folderUri: String? = null,
    /** Seconds; the resume point (0 when finished or never started). */
    val position: Long = 0,
    /** Seconds; 0 when unknown. */
    val length: Long = 0,
    /** 0..1 as computed by the companion; derived from [position]/[length] when absent. */
    val progress: Double? = null,
    val finished: Boolean = false,
    /** Unix ms of the last playback. */
    val lastWatched: Long = 0,
    val playCount: Int = 0,
    val artUrl: String? = null,
    /** `continue` only: the next unfinished episode after the last finished one (not started yet). */
    val next: Boolean = false,
    /** [KIND_YOUTUBE] — a video played in the TV browser ([uri] is its watch page, [artUrl] its thumbnail); `null` — a file. */
    val kind: String? = null,
    /** The YouTube channel of a video. */
    val channel: String? = null,
) {
    val isYouTube: Boolean get() = kind == KIND_YOUTUBE

    /** Fraction watched, 0..1. */
    val fraction: Double
        get() = (progress ?: if (length > 0) position.toDouble() / length else 0.0).coerceIn(0.0, 1.0)

    companion object {
        const val KIND_YOUTUBE = "youtube"
    }
}

/** A page of `getHistory`: [total] is the size of the whole filtered list. */
data class HistoryPage(
    val items: List<HistoryItem> = emptyList(),
    val total: Int = 0,
)

/** `historyUpdate` variants. */
sealed class HistoryChange {
    /** Mark watched (`true`) or unwatched (`false`, resets the position to 0). */
    data class SetFinished(val finished: Boolean) : HistoryChange()

    /** Delete the entry. */
    data object Remove : HistoryChange()
}
