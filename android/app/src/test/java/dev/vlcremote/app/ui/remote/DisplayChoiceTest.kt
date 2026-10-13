package dev.vlcremote.app.ui.remote

import dev.vlcremote.protocol.backend.SystemCommand
import dev.vlcremote.protocol.companion.Display
import dev.vlcremote.protocol.companion.DisplayList
import dev.vlcremote.protocol.model.SystemState
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.model.VlcProcessStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DisplayChoiceTest {

    private val laptop = Display(index = 0, id = "laptop-id", name = "BOE panel", kind = "internal", primary = true)
    private val tv = Display(index = 1, id = "tv-id", name = "LG TV", kind = "external")
    private val newList = DisplayList(listOf(laptop, tv), preferredId = "tv-id")

    private val oldLaptop = Display(index = 0, name = "Laptop", primary = true)
    private val oldTv = Display(index = 1, name = "TV")
    private val oldList = DisplayList(listOf(oldLaptop, oldTv))

    private fun choice(
        list: DisplayList?,
        system: SystemState?,
        vlcOpen: Boolean = true,
        systemAtLoad: SystemState? = system,
        mark: DisplayMark? = null,
    ) = displayChoice(list?.let { DisplaySnapshot(it, systemAtLoad) }, system, vlcOpen, mark)

    // ------------------------------------------------------------------ chip label

    @Test
    fun `chip shows the monitor with VLC when it is open`() {
        val c = choice(newList, SystemState(display = 0, displayId = "laptop-id", preferredDisplayId = "tv-id"))
        assertEquals(laptop, c.chipDisplay)
        assertEquals("tv-id", c.preferredKey)
        assertEquals("tv-id", c.selectedKey) // the radio is the preferred monitor
    }

    @Test
    fun `chip shows the preferred monitor when VLC is closed`() {
        val c = choice(newList, SystemState(displayId = null, preferredDisplayId = "tv-id"), vlcOpen = false)
        assertNull(c.current)
        assertEquals(tv, c.chipDisplay)
    }

    @Test
    fun `chip has no monitor when VLC is closed and nothing is preferred`() {
        val c = choice(newList.copy(preferredId = null), SystemState(preferredDisplayId = null), vlcOpen = false)
        assertNull(c.chipDisplay)
        assertTrue(c.unmanagedSelected)
    }

    @Test
    fun `chip has no monitor before the list is loaded`() {
        val c = choice(null, SystemState(displayId = "tv-id", preferredDisplayId = "tv-id"))
        assertNull(c.chipDisplay)
        assertFalse(c.canChoose)
    }

    @Test
    fun `stale display index is ignored while VLC is closed`() {
        val c = choice(oldList, SystemState(display = 1), vlcOpen = false)
        assertNull(c.currentKey)
        assertNull(c.chipDisplay)
    }

    @Test
    fun `live state wins over a list loaded before it changed`() {
        // List said: VLC on the laptop. Then VLC was dragged to the TV and the companion pushed it.
        val atLoad = SystemState(displayId = "laptop-id", preferredDisplayId = "tv-id")
        val list = DisplayList(listOf(laptop.copy(current = true), tv.copy(preferred = true)), preferredId = "tv-id")
        val c = choice(list, SystemState(displayId = "tv-id", preferredDisplayId = "tv-id"), systemAtLoad = atLoad)
        assertEquals("tv-id", c.chipDisplay?.id)
    }

    @Test
    fun `list flags are used while the state has not changed since it arrived`() {
        val list = DisplayList(listOf(laptop, tv.copy(current = true, preferred = true)), preferredId = null)
        val c = choice(list, SystemState())
        assertEquals("tv-id", c.currentKey)
        assertEquals("tv-id", c.preferredKey)
    }

    @Test
    fun `preferred monitor that is not connected is reported as missing`() {
        val c = choice(DisplayList(listOf(laptop), preferredId = "tv-id"), SystemState(preferredDisplayId = "tv-id"), vlcOpen = false)
        assertTrue(c.preferredMissing)
        assertNull(c.chipDisplay)
        assertFalse(c.unmanagedSelected)
    }

    @Test
    fun `window open without control still counts as open`() {
        assertTrue(VlcInfo(running = false, level = 2, status = VlcProcessStatus.NO_CONTROL).windowOpen)
        assertTrue(VlcInfo(running = true, level = 2).windowOpen)
        assertFalse(VlcInfo(running = false, level = 2, status = VlcProcessStatus.NOT_RUNNING).windowOpen)
        assertFalse(VlcInfo(running = false, level = 2, status = VlcProcessStatus.STARTING).windowOpen)
    }

    // ------------------------------------------------------------------ old companions

    @Test
    fun `old companion moves by index and offers no unmanage`() {
        val c = choice(oldList, SystemState(display = 0))
        assertFalse(c.supportsIds)
        assertFalse(c.canUnmanage)
        assertFalse(c.unmanagedSelected)
        assertFalse(c.preferredMissing)
        assertNull(c.preferredKey)
        assertEquals("#0", c.currentKey)
        assertEquals("#0", c.selectedKey) // radio = where VLC is
        assertEquals(oldLaptop, c.chipDisplay)
        assertEquals(SystemCommand.MoveToDisplay(displayId = null, legacyIndex = 1), c.commandFor(oldTv))
    }

    @Test
    fun `old companion cannot choose while VLC is closed`() {
        val c = choice(oldList, SystemState(display = 0), vlcOpen = false)
        assertFalse(c.canChoose)
        assertNull(c.chipDisplay)
    }

    @Test
    fun `new companion moves by id and can choose while VLC is closed`() {
        val c = choice(newList, SystemState(preferredDisplayId = "tv-id"), vlcOpen = false)
        assertTrue(c.canChoose)
        assertTrue(c.canUnmanage)
        assertEquals(SystemCommand.MoveToDisplay("laptop-id", legacyIndex = null), c.commandFor(laptop))
    }

    // ------------------------------------------------------------------ optimistic marking

    @Test
    fun `after a move the target is marked until the companion reports`() {
        val before = SystemState(display = 0, displayId = "laptop-id", preferredDisplayId = "laptop-id")
        val list = newList.copy(preferredId = "laptop-id")
        val initial = choice(list, before)
        assertEquals(laptop, initial.chipDisplay)

        val mark = initial.markAfterMove(tv, before)
        val marked = choice(list, before, mark = mark)
        assertEquals("tv-id", marked.currentKey)
        assertEquals("tv-id", marked.preferredKey)
        assertEquals(tv, marked.chipDisplay)

        // The companion pushed the new state: the mark gives way to it.
        val after = SystemState(display = 1, displayId = "tv-id", preferredDisplayId = "tv-id")
        val reported = choice(list, after, systemAtLoad = before, mark = mark)
        assertEquals("tv-id", reported.currentKey)
        assertEquals("tv-id", reported.preferredKey)

        // ...and when it says otherwise (the move did not happen), the state wins.
        val other = SystemState(display = 0, displayId = "laptop-id", preferredDisplayId = "tv-id")
        assertEquals("laptop-id", choice(list, other, systemAtLoad = before, mark = mark).currentKey)
    }

    @Test
    fun `move while VLC is closed only marks the preferred monitor`() {
        val before = SystemState(preferredDisplayId = null)
        val list = newList.copy(preferredId = null)
        val c = choice(list, before, vlcOpen = false)
        val mark = c.markAfterMove(tv, before)
        assertNull(mark.currentKey)
        val marked = choice(list, before, vlcOpen = false, mark = mark)
        assertEquals("tv-id", marked.preferredKey)
        assertNull(marked.currentKey)
        assertEquals(tv, marked.chipDisplay)
    }

    @Test
    fun `unmanage is marked at once`() {
        val before = SystemState(displayId = "tv-id", preferredDisplayId = "tv-id")
        val c = choice(newList, before)
        val marked = choice(newList, before, mark = c.markAfterUnmanage(before))
        assertTrue(marked.unmanagedSelected)
        assertNull(marked.selectedKey)
        assertEquals(tv, marked.chipDisplay) // VLC itself stays where it is
    }

    @Test
    fun `old companion move marks only where VLC is`() {
        val before = SystemState(display = 0)
        val c = choice(oldList, before)
        val mark = c.markAfterMove(oldTv, before)
        assertFalse(mark.preferredSet)
        val marked = choice(oldList, before, mark = mark)
        assertEquals("#1", marked.currentKey)
        assertEquals("#1", marked.selectedKey)
        assertNull(marked.preferredKey)
    }
}
