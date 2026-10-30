package app.sofabird.ui.touchpad

import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.backend.PcKey
import app.sofabird.ui.touchpad.KeyboardStream.Companion.SENTINEL
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TouchpadInputTest {

    private val backspace = PcInput.Key(PcKey.BACKSPACE)
    private val enter = PcInput.Key(PcKey.ENTER)

    // ------------------------------------------------------------------ keyboard

    @Test
    fun `typing goes to the PC as it is typed`() {
        val k = KeyboardStream()
        assertEquals(SENTINEL, k.field)
        assertEquals(listOf(PcInput.Text("h")), k.edit(SENTINEL + "h"))
        assertEquals(listOf(PcInput.Text("i")), k.edit(SENTINEL + "hi"))
        assertEquals(listOf(PcInput.Text(" there")), k.edit(SENTINEL + "hi there")) // a word committed at once
        assertEquals(SENTINEL + "hi there", k.field)
    }

    @Test
    fun `a selection change sends nothing`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "abc")
        assertEquals(emptyList<PcInput>(), k.edit(SENTINEL + "abc"))
    }

    @Test
    fun `a replaced word is erased back to the change and typed again`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "teh")
        assertEquals(listOf(backspace, backspace, PcInput.Text("he ")), k.edit(SENTINEL + "the "))
    }

    @Test
    fun `backspace in the field erases on the PC`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "ab")
        assertEquals(listOf(backspace), k.edit(SENTINEL + "a"))
        assertEquals(listOf(backspace), k.edit(SENTINEL))
    }

    @Test
    fun `backspace in a field that looks empty still reaches the PC`() {
        val k = KeyboardStream()
        // The IME erased the sentinel: one Backspace, and the field gets its sentinel back.
        assertEquals(listOf(backspace), k.edit(""))
        assertEquals(SENTINEL, k.field)
        assertEquals(listOf(backspace), k.edit(""))
    }

    @Test
    fun `erasing everything at once is no extra backspace`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "abc")
        assertEquals(listOf(backspace, backspace, backspace), k.edit(""))
        assertEquals(SENTINEL, k.field)
    }

    @Test
    fun `an emoji is one backspace`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "ok😀")
        assertEquals(listOf(backspace), k.edit(SENTINEL + "ok"))
    }

    @Test
    fun `enter from the IME presses enter and the field starts over`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "query")
        assertEquals(listOf(enter), k.edit(SENTINEL + "query\n"))
        assertEquals(SENTINEL, k.field)
        assertEquals(listOf(PcInput.Text("x")), k.edit(SENTINEL + "x"))
    }

    @Test
    fun `a new line inside the text splits it there`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "abcd")
        assertEquals(listOf(backspace, backspace, enter, PcInput.Text("cd")), k.edit(SENTINEL + "ab\ncd"))
        assertEquals(SENTINEL + "cd", k.field)
    }

    @Test
    fun `pasted lines are typed with enter between them`() {
        val k = KeyboardStream()
        assertEquals(listOf(PcInput.Text("one"), enter, PcInput.Text("two")), k.edit(SENTINEL + "one\ntwo"))
    }

    @Test
    fun `a long line starts over after a space`() {
        val k = KeyboardStream()
        val long = "a".repeat(KeyboardStream.MAX_LINE)
        k.edit(SENTINEL + long)
        assertEquals(SENTINEL + long, k.field)
        assertEquals(listOf(PcInput.Text(" ")), k.edit(SENTINEL + long + " "))
        assertEquals(SENTINEL, k.field)
    }

    @Test
    fun `special keys keep the field in step`() {
        val k = KeyboardStream()
        k.edit(SENTINEL + "abc")
        assertEquals(listOf(backspace), k.key(PcKey.BACKSPACE))
        assertEquals(SENTINEL + "ab", k.field)
        assertEquals(listOf(backspace), k.edit(SENTINEL + "a")) // the field and the PC still agree

        // An arrow moves the PC's cursor away from the field's text: the field starts over.
        assertEquals(listOf(PcInput.Key(PcKey.LEFT)), k.key(PcKey.LEFT))
        assertEquals(SENTINEL, k.field)
        k.edit(SENTINEL + "xy")
        assertEquals(listOf(enter), k.key(PcKey.ENTER))
        assertEquals(SENTINEL, k.field)
        assertEquals(listOf(backspace), k.key(PcKey.BACKSPACE)) // nothing left to shorten
        assertEquals(SENTINEL, k.field)
    }

    // ------------------------------------------------------------------ pointer and wheel

    private fun PointerMotion.total(steps: Int, dx: Float, dy: Float, dtMs: Long): Pair<Int, Int> {
        var x = 0
        var y = 0
        repeat(steps) { move(dx, dy, dtMs)?.let { x += it.dx; y += it.dy } }
        return x to y
    }

    @Test
    fun `slow strokes add their fractions up`() {
        val motion = PointerMotion()
        assertNull(motion.move(0.2f, 0f, 16)) // 0.3 px: kept for later
        motion.reset()
        // 2 dp at a crawl: SENSITIVITY pixels per dp, nothing lost to rounding.
        assertEquals(3 to -3, motion.total(10, 0.2f, -0.2f, 16))
    }

    @Test
    fun `fast strokes go further than slow ones over the same distance`() {
        val slow = PointerMotion().total(60, 1f, 0f, 50).first // 60 dp at 0.02 dp/ms
        val fast = PointerMotion().total(4, 15f, 0f, 4).first // 60 dp at 3.75 dp/ms, a flick
        assertTrue("slow $slow", slow in 90..92)
        assertTrue("fast $fast slow $slow", fast > slow * 2)
        assertTrue("fast $fast", fast <= 60 * PointerMotion.SENSITIVITY * PointerMotion.MAX_BOOST)
    }

    @Test
    fun `an ordinary stroke is accelerated well past a creeping one`() {
        val slow = PointerMotion().total(60, 1f, 0f, 50).first // 60 dp at 0.02 dp/ms
        val ordinary = PointerMotion().total(6, 10f, 0f, 10).first // 60 dp at 1 dp/ms (~16 cm/s)
        assertTrue("ordinary $ordinary slow $slow", ordinary > slow * 2)
    }

    @Test
    fun `the wheel follows the fingers like the phone's own lists`() {
        val scroll = ScrollMotion()
        scroll.start(1f, -20f) // up: a vertical stroke
        assertEquals(PcInput.Scroll(dy = 40, dx = 0), scroll.scroll(3f, -10f)) // fingers up — scroll down; no sideways drift
        assertEquals(PcInput.Scroll(dy = -4, dx = 0), scroll.scroll(0f, 1f))
        assertNull(scroll.scroll(0f, 0.1f)) // less than a wheel unit: kept

        scroll.start(-20f, 2f) // sideways
        assertEquals(PcInput.Scroll(dy = 0, dx = 40), scroll.scroll(-10f, 5f))
    }
}
