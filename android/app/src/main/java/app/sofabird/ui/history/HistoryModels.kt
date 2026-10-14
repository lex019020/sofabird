package app.sofabird.ui.history

import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.ui.common.displayNameFromUri
import java.time.Clock
import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.temporal.TemporalAdjusters

/*
 * Presentation logic of the watch history (companion `history` capability), kept free of Android so
 * it is unit-tested: date groups of the History screen, the "Continue watching" row, folder badges.
 */

/** Title to show: VLC's title, else the file name, else the last URI segment. */
val HistoryItem.displayTitle: String
    get() = title.takeIf { it.isNotBlank() } ?: name?.takeIf { it.isNotBlank() } ?: displayNameFromUri(uri)

/** Folder name for the second line of a history row; `null` when unknown. */
val HistoryItem.folderName: String?
    get() = folderUri?.takeIf { it.isNotBlank() }?.let(::displayNameFromUri)?.takeIf { it.isNotBlank() }

/** Started and not finished: worth a progress bar and "1:23:45 of 2:58:25". */
val HistoryItem.isStarted: Boolean
    get() = !finished && position > 0

// ------------------------------------------------------------------ History screen: date groups

enum class DateGroup { TODAY, YESTERDAY, THIS_WEEK, EARLIER }

data class HistoryGroup(val group: DateGroup, val items: List<HistoryItem>)

/**
 * Splits [items] (newest first, as the companion sends them) into Today / Yesterday / This week /
 * Earlier by [HistoryItem.lastWatched] in the [clock]'s time zone. "This week" is the calendar week
 * that starts on [firstDayOfWeek] (locale-dependent), without today and yesterday. Empty groups are
 * left out; the order inside a group is kept.
 */
fun groupHistoryByDate(
    items: List<HistoryItem>,
    clock: Clock,
    firstDayOfWeek: DayOfWeek = DayOfWeek.MONDAY,
): List<HistoryGroup> {
    val zone = clock.zone
    val today = LocalDate.now(clock)
    val yesterday = today.minusDays(1)
    val weekStart = today.with(TemporalAdjusters.previousOrSame(firstDayOfWeek))
    val byGroup = items.groupBy { item ->
        if (item.lastWatched <= 0) return@groupBy DateGroup.EARLIER
        val day = Instant.ofEpochMilli(item.lastWatched).atZone(zone).toLocalDate()
        when {
            !day.isBefore(today) -> DateGroup.TODAY // also a PC clock slightly ahead of the phone
            day == yesterday -> DateGroup.YESTERDAY
            !day.isBefore(weekStart) -> DateGroup.THIS_WEEK
            else -> DateGroup.EARLIER
        }
    }
    return DateGroup.entries.mapNotNull { g -> byGroup[g]?.let { HistoryGroup(g, it) } }
}

// ------------------------------------------------------------------ Library: "Continue watching"

/** One card of the "Continue watching" row. */
data class ContinueCard(
    val item: HistoryItem,
    /** 0..1 for the progress bar; 0 for a next episode. */
    val fraction: Float,
    /** The next unfinished episode of a series folder (not started): "Next episode" badge instead of times. */
    val isNext: Boolean,
) {
    val uri: String get() = item.uri
    val title: String get() = item.displayTitle
    val position: Long get() = item.position
    val length: Long get() = item.length
}

/**
 * Cards for `getHistory continue` items: finished entries are dropped (a stale page right after
 * "mark watched"), duplicates by URI too; the companion's order (last watched first) is kept.
 */
fun continueCards(items: List<HistoryItem>, limit: Int = CONTINUE_LIMIT): List<ContinueCard> =
    items.asSequence()
        .filter { !it.finished && it.uri.isNotBlank() }
        .distinctBy { it.uri }
        .take(limit)
        .map { item ->
            val isNext = item.next && item.position <= 0
            ContinueCard(item, if (isNext) 0f else item.fraction.toFloat(), isNext)
        }
        .toList()

const val CONTINUE_LIMIT = 20

// ------------------------------------------------------------------ folder listings

/** What a file row of a folder shows about the history. */
sealed interface EntryBadge {
    /** Not in the history (or not started). */
    data object None : EntryBadge

    /** Started: a thin progress bar. */
    data class Started(val fraction: Float) : EntryBadge

    /** Watched to the end (or marked watched): a check mark. */
    data object Finished : EntryBadge
}

/** Badge from the optional `progress` / `finished` fields of a `browse` entry. Folders never get one. */
fun Entry.historyBadge(): EntryBadge {
    if (isDir) return EntryBadge.None
    if (finished == true) return EntryBadge.Finished
    val p = progress ?: return EntryBadge.None
    if (p.isNaN() || p <= 0.0) return EntryBadge.None
    return EntryBadge.Started(p.coerceIn(0.0, 1.0).toFloat())
}
