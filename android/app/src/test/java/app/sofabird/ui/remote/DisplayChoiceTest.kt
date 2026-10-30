package app.sofabird.ui.remote

import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.companion.Display
import app.sofabird.protocol.companion.DisplayList
import app.sofabird.protocol.model.SystemState
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.model.VlcProcessStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DisplayChoiceTest {

    private val laptop = Display(id = "laptop-id", name = "BOE panel", kind = "internal", primary = true)
    private val tv = Display(id = "tv-id", name = "LG TV", kind = "external")
    private val displays = DisplayList(listOf(laptop, tv), preferredId = "tv-id")

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
        val c = choice(displays, SystemState(displayId = "laptop-id", preferredDisplayId = "tv-id"))
        assertEquals(laptop, c.chipDisplay)
        assertEquals("tv-id", c.preferredId)
        assertEquals("tv-id", c.selectedId) // the radio is the preferred monitor
    }

    @Test
    fun `chip shows the preferred monitor when VLC is closed`() {
        val c = choice(displays, SystemState(displayId = null, preferredDisplayId = "tv-id"), vlcOpen = false)
        assertNull(c.current)
        assertEquals(tv, c.chipDisplay)
    }

    @Test
    fun `chip has no monitor when VLC is closed and nothing is preferred`() {
        val c = choice(displays.copy(preferredId = null), SystemState(preferredDisplayId = null), vlcOpen = false)
        assertNull(c.chipDisplay)
        assertTrue(c.unmanagedSelected)
    }

    @Test
    fun `chip has no monitor before the list is loaded`() {
        val c = choice(null, SystemState(displayId = "tv-id", preferredDisplayId = "tv-id"))
        assertNull(c.chipDisplay)
        assertFalse(c.canChoose)
        assertFalse(c.unmanagedSelected)
        assertFalse(c.preferredMissing)
    }

    @Test
    fun `stale window position is ignored while VLC is closed`() {
        val c = choice(displays, SystemState(displayId = "laptop-id", preferredDisplayId = "tv-id"), vlcOpen = false)
        assertNull(c.currentId)
        assertEquals(tv, c.chipDisplay)
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
        assertEquals("tv-id", c.currentId)
        assertEquals("tv-id", c.preferredId)
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
        assertTrue(VlcInfo(running = true, level = 2, status = VlcProcessStatus.RUNNING).windowOpen)
        assertFalse(VlcInfo(running = false, level = 2, status = VlcProcessStatus.NOT_RUNNING).windowOpen)
        assertFalse(VlcInfo(running = false, level = 2, status = VlcProcessStatus.STARTING).windowOpen)
    }

    @Test
    fun `moves by id and can choose while VLC is closed`() {
        val c = choice(displays, SystemState(preferredDisplayId = "tv-id"), vlcOpen = false)
        assertTrue(c.canChoose)
        assertEquals(SystemCommand.MoveToDisplay("laptop-id"), c.commandFor(laptop))
    }

    @Test
    fun `a monitor without a name is numbered by its place in the list`() {
        val unnamed = Display(id = "x", name = "")
        val c = choice(DisplayList(listOf(laptop, unnamed)), SystemState())
        assertEquals(2, c.numberOf(unnamed))
        assertEquals(1, c.numberOf(laptop))
    }

    // ------------------------------------------------------------------ optimistic marking

    @Test
    fun `after a move the target is marked until the companion reports`() {
        val before = SystemState(displayId = "laptop-id", preferredDisplayId = "laptop-id")
        val list = displays.copy(preferredId = "laptop-id")
        val initial = choice(list, before)
        assertEquals(laptop, initial.chipDisplay)

        val mark = initial.markAfterMove(tv, before)
        val marked = choice(list, before, mark = mark)
        assertEquals("tv-id", marked.currentId)
        assertEquals("tv-id", marked.preferredId)
        assertEquals(tv, marked.chipDisplay)

        // The companion pushed the new state: the mark gives way to it.
        val after = SystemState(displayId = "tv-id", preferredDisplayId = "tv-id")
        val reported = choice(list, after, systemAtLoad = before, mark = mark)
        assertEquals("tv-id", reported.currentId)
        assertEquals("tv-id", reported.preferredId)

        // ...and when it says otherwise (the move did not happen), the state wins.
        val other = SystemState(displayId = "laptop-id", preferredDisplayId = "tv-id")
        assertEquals("laptop-id", choice(list, other, systemAtLoad = before, mark = mark).currentId)
    }

    @Test
    fun `move while VLC is closed only marks the preferred monitor`() {
        val before = SystemState(preferredDisplayId = null)
        val list = displays.copy(preferredId = null)
        val c = choice(list, before, vlcOpen = false)
        val mark = c.markAfterMove(tv, before)
        assertNull(mark.currentId)
        val marked = choice(list, before, vlcOpen = false, mark = mark)
        assertEquals("tv-id", marked.preferredId)
        assertNull(marked.currentId)
        assertEquals(tv, marked.chipDisplay)
    }

    @Test
    fun `unmanage is marked at once`() {
        val before = SystemState(displayId = "tv-id", preferredDisplayId = "tv-id")
        val c = choice(displays, before)
        val marked = choice(displays, before, mark = c.markAfterUnmanage(before))
        assertTrue(marked.unmanagedSelected)
        assertNull(marked.selectedId)
        assertEquals(tv, marked.chipDisplay) // VLC itself stays where it is
    }
}
