package app.sofabird.protocol.backend

/**
 * The phone as a touchpad and keyboard for the PC (`input` messages, capability `input`; protocol/README.md "Touchpad
 * and keyboard"). Injected on the PC into whatever has the focus; a Moonlight stream passes it on to its computer.
 * Fire-and-forget: no reply.
 */
sealed interface PcInput {
    /** Relative mouse move in pixels. */
    data class Move(val dx: Int, val dy: Int) : PcInput

    data class Click(val button: MouseButton = MouseButton.LEFT) : PcInput

    /** Holds a button down (dragging) until [Release]. */
    data class Press(val button: MouseButton = MouseButton.LEFT) : PcInput

    data class Release(val button: MouseButton = MouseButton.LEFT) : PcInput

    /** The wheel in wheel units ([WHEEL_NOTCH] = one notch): positive [dy] scrolls down, positive [dx] right. */
    data class Scroll(val dy: Int, val dx: Int = 0) : PcInput

    /** Typed as Unicode characters on the PC (any language, no keyboard layout needed). */
    data class Text(val text: String) : PcInput

    /** A key, with [mods] held around it. */
    data class Key(val key: PcKey, val mods: Set<KeyMod> = emptySet()) : PcInput

    companion object {
        /** One wheel notch (Windows' `WHEEL_DELTA`). */
        const val WHEEL_NOTCH = 120

        /** The most a message moves or scrolls (the companion clamps to the same). */
        const val MAX_DELTA = 4000
    }
}

enum class MouseButton(val wire: String) {
    LEFT("left"),
    RIGHT("right"),
    MIDDLE("middle"),
}

/** The keys the companion knows (`key` of an `input` message). */
enum class PcKey(val wire: String) {
    ENTER("enter"),
    BACKSPACE("backspace"),
    TAB("tab"),
    ESCAPE("escape"),
    SPACE("space"),
    LEFT("left"),
    UP("up"),
    RIGHT("right"),
    DOWN("down"),
    HOME("home"),
    END("end"),
    PAGE_UP("pageup"),
    PAGE_DOWN("pagedown"),
    DELETE("delete"),
    WIN("win"),
    F4("f4"),
    F5("f5"),
    F11("f11"),
    VOLUME_UP("volumeup"),
    VOLUME_DOWN("volumedown"),
    VOLUME_MUTE("volumemute"),
    A("a"),
    C("c"),
    D("d"),
    L("l"),
    M("m"),
    Q("q"),
    S("s"),
    V("v"),
    Z("z"),
}

enum class KeyMod(val wire: String) {
    CTRL("ctrl"),
    ALT("alt"),
    SHIFT("shift"),
    WIN("win"),
}

/**
 * Moonlight's own shortcuts during a stream: Ctrl+Alt+Shift with a letter, typed into the stream window on the TV PC
 * (Moonlight takes them itself and does not pass them on). [MINIMIZE] and [DISCONNECT] (the app keeps running on the
 * other computer) are known too; the remote offers the two that are safe to press from the sofa.
 */
enum class MoonlightShortcut(val key: PcKey) {
    /** The performance overlay: frame rate, latency, dropped frames. */
    STATS(PcKey.S),

    /** The mouse between "remote desktop" (the cursor where it is) and "game" mode (captured, relative). */
    MOUSE_MODE(PcKey.M),

    /** Minimises the stream window (it keeps running behind; the TV PC's own desktop is then under the touchpad). */
    MINIMIZE(PcKey.D),

    /** Leaves the stream; the app keeps running on the other computer. */
    DISCONNECT(PcKey.Q),
    ;

    val input: PcInput.Key get() = PcInput.Key(key, setOf(KeyMod.CTRL, KeyMod.ALT, KeyMod.SHIFT))
}

/**
 * Inputs on their way to the PC, with the touchpad's moves merged: consecutive [PcInput.Move]s (and consecutive
 * [PcInput.Scroll]s) become one, so a frame sends at most one of each; everything else keeps its place, so a button's
 * press and release never swap with the moves between them.
 */
class InputBatch {
    private val pending = ArrayDeque<PcInput>()

    val isEmpty: Boolean get() = pending.isEmpty()

    fun add(input: PcInput) {
        val last = pending.lastOrNull()
        val merged = when {
            input is PcInput.Move && last is PcInput.Move -> PcInput.Move(last.dx + input.dx, last.dy + input.dy)
            input is PcInput.Scroll && last is PcInput.Scroll -> PcInput.Scroll(last.dy + input.dy, last.dx + input.dx)
            else -> null
        }
        if (merged != null) pending[pending.lastIndex] = merged else pending.addLast(input)
    }

    /** Everything added since the last call, in order; moves and scrolls that add up to nothing are left out. */
    fun drain(): List<PcInput> {
        val out = pending.filterNot { it.isNothing }
        pending.clear()
        return out
    }

    private val PcInput.isNothing: Boolean
        get() = (this is PcInput.Move && dx == 0 && dy == 0) || (this is PcInput.Scroll && dx == 0 && dy == 0)
}
