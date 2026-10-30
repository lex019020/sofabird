package app.sofabird.ui.touchpad

import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.backend.PcKey
import kotlin.math.abs
import kotlin.math.hypot

/** Whole units of a movement given in fractions; the fraction waits for the next one, so slow strokes are not lost. */
internal class Remainder {
    private var x = 0f
    private var y = 0f

    fun take(dx: Float, dy: Float): Pair<Int, Int> {
        x += dx
        y += dy
        val wholeX = x.toInt()
        val wholeY = y.toInt()
        x -= wholeX
        y -= wholeY
        return wholeX to wholeY
    }

    fun reset() {
        x = 0f
        y = 0f
    }
}

/**
 * One finger on the touchpad → the PC's pointer: [sensitivity] pixels per dp the finger travels, and a little more for a
 * fast stroke (up to [MAX_BOOST] times from [FAST_DP_PER_MS]): a slow finger is precise, a flick crosses the screen.
 * Whole pixels are sent; the fraction is carried over to the next event.
 */
class PointerMotion(private val sensitivity: Float = SENSITIVITY) {
    private val rest = Remainder()
    private var speed = 0f

    /** A new stroke: no speed, no fraction left over from the last one. */
    fun reset() {
        rest.reset()
        speed = 0f
    }

    /** [dx], [dy] in dp since the previous event, [dtMs] after it; `null` while it adds up to less than a pixel. */
    fun move(dx: Float, dy: Float, dtMs: Long): PcInput.Move? {
        val instant = hypot(dx, dy) / dtMs.coerceAtLeast(MIN_DT_MS)
        speed += (instant - speed) * SPEED_SMOOTHING
        val gain = sensitivity * (1 + (MAX_BOOST - 1) * (speed / FAST_DP_PER_MS).coerceAtMost(1f))
        val (x, y) = rest.take(dx * gain, dy * gain)
        return if (x == 0 && y == 0) null else PcInput.Move(x, y)
    }

    companion object {
        /** Pixels on the PC per dp of a slow finger. */
        const val SENSITIVITY = 1.5f

        /** A fast stroke moves the pointer this many times further than a slow one. */
        const val MAX_BOOST = 2.5f

        /** From this speed (dp per ms, ~40 cm/s) on, a stroke gets the whole [MAX_BOOST]. */
        const val FAST_DP_PER_MS = 2.5f

        /** How much of a new event's speed counts (the rest is the speed so far): one jittery event does not jump. */
        private const val SPEED_SMOOTHING = 0.5f
        private const val MIN_DT_MS = 4L
    }
}

/**
 * Two fingers on the touchpad → the PC's wheel, [WHEEL_PER_DP] wheel units per dp. The content follows the fingers, as on
 * the phone: fingers up — scroll down. A stroke scrolls along the axis it started on ([start]), so a vertical scroll
 * does not drift sideways.
 */
class ScrollMotion {
    private val rest = Remainder()
    private var horizontal = false

    /** A new two-finger stroke that has moved by [dx], [dy] (dp) so far: decides its axis. */
    fun start(dx: Float, dy: Float) {
        rest.reset()
        horizontal = abs(dx) > abs(dy)
    }

    /** [dx], [dy] in dp since the previous event; `null` while it adds up to less than a wheel unit. */
    fun scroll(dx: Float, dy: Float): PcInput.Scroll? {
        val (x, y) = if (horizontal) rest.take(-dx * WHEEL_PER_DP, 0f) else rest.take(0f, -dy * WHEEL_PER_DP)
        return if (x == 0 && y == 0) null else PcInput.Scroll(dy = y, dx = x)
    }

    companion object {
        /** A wheel notch ([PcInput.WHEEL_NOTCH]) for every 30 dp (~5 mm) of finger travel. */
        const val WHEEL_PER_DP = 4f
    }
}

/**
 * The keyboard's text field, typed on the PC as it changes: what the field gained becomes `text`, what it lost
 * Backspaces (an edit inside the text — an IME's suggestion — is erased back to there and typed again), and a new line
 * `key enter`, after which the field starts over. So it only holds the current line ([MAX_LINE] at most: past that it
 * starts over after a space).
 *
 * The field begins with an invisible [SENTINEL]: keyboards send nothing for Backspace in an empty field, so the field is
 * never empty; erasing the sentinel is a Backspace on the PC, and the sentinel comes back.
 */
class KeyboardStream {
    private var typed = ""

    /** What the field shows now. */
    val field: String get() = SENTINEL + typed

    /** The field changed to [field] (by the IME): the inputs that make the PC follow. Afterwards show [KeyboardStream.field]. */
    fun edit(field: String): List<PcInput> {
        val sentinelErased = !field.startsWith(SENTINEL)
        val lines = field.replace(SENTINEL, "").split('\n')
        val out = mutableListOf<PcInput>()
        // Backspace in a field that looked empty: the character before what the field holds, on the PC.
        if (sentinelErased && typed.isEmpty() && field.isEmpty()) out += PcInput.Key(PcKey.BACKSPACE)
        diff(typed, lines.first(), out)
        lines.drop(1).forEach { line ->
            out += PcInput.Key(PcKey.ENTER)
            if (line.isNotEmpty()) out += PcInput.Text(line)
        }
        typed = lines.last()
        if (typed.length > MAX_LINE && typed.endsWith(' ')) typed = ""
        return out
    }

    /**
     * A key of the special row: Backspace and Enter also change the field as they change the PC's text; any other key
     * (an arrow, Tab, Esc…) moves the PC's cursor away from what the field holds, so the field starts over.
     */
    fun key(key: PcKey): List<PcInput> {
        typed = when (key) {
            PcKey.BACKSPACE -> typed.dropLastCodePoint()
            else -> ""
        }
        return listOf(PcInput.Key(key))
    }

    private fun diff(before: String, after: String, out: MutableList<PcInput>) {
        val common = before.commonPrefixWith(after).length // never splits a surrogate pair
        repeat(before.codePointCount(common, before.length)) { out += PcInput.Key(PcKey.BACKSPACE) }
        if (after.length > common) out += PcInput.Text(after.substring(common))
    }

    private fun String.dropLastCodePoint(): String = if (isEmpty()) this else substring(0, offsetByCodePoints(length, -1))

    companion object {
        /** A zero-width space: invisible, but a character the IME can erase. */
        const val SENTINEL = "​"
        const val MAX_LINE = 40
    }
}
