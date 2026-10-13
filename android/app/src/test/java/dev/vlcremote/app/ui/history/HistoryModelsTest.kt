package dev.vlcremote.app.ui.history

import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.EntryType
import dev.vlcremote.protocol.model.HistoryItem
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Clock
import java.time.DayOfWeek
import java.time.LocalDateTime
import java.time.ZoneId

class HistoryModelsTest {

    private val zone = ZoneId.of("Europe/Berlin")

    /** Thursday 2026-10-15 20:00 local. */
    private val clock: Clock = Clock.fixed(LocalDateTime.of(2026, 10, 15, 20, 0).atZone(zone).toInstant(), zone)

    private fun at(y: Int, mo: Int, d: Int, h: Int = 12, mi: Int = 0): Long =
        LocalDateTime.of(y, mo, d, h, mi).atZone(zone).toInstant().toEpochMilli()

    private fun item(uri: String, lastWatched: Long, finished: Boolean = false, position: Long = 0, length: Long = 0, next: Boolean = false) =
        HistoryItem(uri = uri, title = uri, position = position, length = length, finished = finished, lastWatched = lastWatched, next = next)

    @Test
    fun `groups by local date with a calendar week`() {
        val items = listOf(
            item("today-late", at(2026, 10, 15, 19, 59)),
            item("today-early", at(2026, 10, 15, 0, 1)),
            item("yesterday", at(2026, 10, 14, 23, 59)),
            item("monday", at(2026, 10, 12, 8)),
            item("last-sunday", at(2026, 10, 11, 22)),
            item("unknown", 0),
        )
        val groups = groupHistoryByDate(items, clock, DayOfWeek.MONDAY)
        assertEquals(listOf(DateGroup.TODAY, DateGroup.YESTERDAY, DateGroup.THIS_WEEK, DateGroup.EARLIER), groups.map { it.group })
        assertEquals(listOf("today-late", "today-early"), groups[0].items.map { it.uri })
        assertEquals(listOf("yesterday"), groups[1].items.map { it.uri })
        assertEquals(listOf("monday"), groups[2].items.map { it.uri })
        assertEquals(listOf("last-sunday", "unknown"), groups[3].items.map { it.uri })
    }

    @Test
    fun `week start follows the locale and empty groups are left out`() {
        val sunday = item("sunday", at(2026, 10, 11, 22))
        // With Sunday as the first day of the week, last Sunday belongs to this week.
        assertEquals(DateGroup.THIS_WEEK, groupHistoryByDate(listOf(sunday), clock, DayOfWeek.SUNDAY).single().group)
        assertEquals(DateGroup.EARLIER, groupHistoryByDate(listOf(sunday), clock, DayOfWeek.MONDAY).single().group)
        assertTrue(groupHistoryByDate(emptyList(), clock).isEmpty())
    }

    @Test
    fun `on a monday yesterday is not this week`() {
        val monday = Clock.fixed(LocalDateTime.of(2026, 10, 12, 9, 0).atZone(zone).toInstant(), zone)
        val groups = groupHistoryByDate(listOf(item("sun", at(2026, 10, 11, 21)), item("sat", at(2026, 10, 10))), monday)
        assertEquals(listOf(DateGroup.YESTERDAY, DateGroup.EARLIER), groups.map { it.group })
    }

    @Test
    fun `a PC clock slightly ahead still counts as today`() {
        assertEquals(DateGroup.TODAY, groupHistoryByDate(listOf(item("ahead", at(2026, 10, 16, 0, 5))), clock).single().group)
    }

    @Test
    fun `continue cards keep order, drop finished and duplicates`() {
        val items = listOf(
            item("a", at(2026, 10, 15), position = 3600, length = 7200),
            item("b", at(2026, 10, 14), next = true),
            item("a", at(2026, 10, 13), position = 100, length = 7200),
            item("done", at(2026, 10, 12), finished = true),
            item("c", at(2026, 10, 11), position = 900, length = 0),
        )
        val cards = continueCards(items)
        assertEquals(listOf("a", "b", "c"), cards.map { it.uri })
        assertEquals(0.5f, cards[0].fraction, 1e-6f)
        assertEquals(false, cards[0].isNext)
        assertEquals(true, cards[1].isNext)
        assertEquals(0f, cards[1].fraction, 0f)
        assertEquals(0f, cards[2].fraction, 0f) // unknown length: an empty bar, times still shown
        assertEquals(900L, cards[2].position)
    }

    @Test
    fun `continue cards are capped and a started next item shows its progress`() {
        val many = (1..30).map { item("u$it", it.toLong(), position = 400, length = 1000) }
        assertEquals(CONTINUE_LIMIT, continueCards(many).size)
        val started = continueCards(listOf(item("n", 1, position = 500, length = 1000, next = true))).single()
        assertEquals(false, started.isNext)
        assertEquals(0.5f, started.fraction, 1e-6f)
    }

    @Test
    fun `titles fall back to file name and uri`() {
        assertEquals("x.mkv", HistoryItem("file:///D:/a/x.mkv", title = "", name = "x.mkv").displayTitle)
        assertEquals("Мой фильм.mkv", HistoryItem("file:///D:/a/%D0%9C%D0%BE%D0%B9%20%D1%84%D0%B8%D0%BB%D1%8C%D0%BC.mkv").displayTitle)
        assertEquals("Season 1", HistoryItem("file:///D:/Season%201/e.mkv", folderUri = "file:///D:/Season%201/").folderName)
        assertEquals("Y:", HistoryItem("file:///y:/e.mkv", folderUri = "file:///y:/").folderName)
        assertEquals("C++ course", HistoryItem("file:///D:/C++%20course/e.mkv", folderUri = "file:///D:/C++%20course/").folderName)
    }

    @Test
    fun `folder entry badges`() {
        fun file(progress: Double?, finished: Boolean?) =
            Entry("x.mkv", "file:///x.mkv", EntryType.FILE, progress = progress, finished = finished)
        assertEquals(EntryBadge.None, file(null, null).historyBadge())
        assertEquals(EntryBadge.None, file(0.0, false).historyBadge())
        assertEquals(EntryBadge.Started(0.4f), file(0.4, false).historyBadge())
        assertEquals(EntryBadge.Started(0.4f), file(0.4, null).historyBadge())
        assertEquals(EntryBadge.Started(1f), file(1.3, false).historyBadge())
        assertEquals(EntryBadge.Finished, file(1.0, true).historyBadge())
        assertEquals(EntryBadge.Finished, file(null, true).historyBadge())
        assertEquals(EntryBadge.None, file(Double.NaN, false).historyBadge())
        assertEquals(EntryBadge.None, Entry("d", "file:///d/", EntryType.DIR, progress = 0.5, finished = true).historyBadge())
    }
}
