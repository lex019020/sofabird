package app.sofabird.ui.remote

import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.companion.Display
import app.sofabird.protocol.companion.DisplayList
import app.sofabird.protocol.model.SystemState

/**
 * Monitor choice for the VLC window ("Display for VLC"), derived from three sources, newest wins:
 * 1. [DisplayMark] — local marking right after a successful action, until the companion pushes a new
 *    `state.system`;
 * 2. the `listDisplays` answer, while `state.system` is the same as when the list arrived;
 * 3. live `state.system` (`displayId`, `preferredDisplayId`).
 *
 * Monitors are named by their stable [Display.id].
 */

/** `listDisplays` answer plus the `state.system` seen when it arrived (to tell which one is newer). */
data class DisplaySnapshot(val list: DisplayList, val systemAtLoad: SystemState?)

/**
 * Optimistic marking after an action succeeded. Applies while `state.system` still equals [baseline]
 * (the companion has not reported anything newer).
 */
data class DisplayMark(
    val baseline: SystemState?,
    /** Monitor the VLC window was moved to (`null` = the window was not moved). */
    val currentId: String? = null,
    /** [preferredId] is meaningful (`null` there = "don't manage"). */
    val preferredSet: Boolean = false,
    val preferredId: String? = null,
)

data class DisplayChoice(
    val items: List<Display> = emptyList(),
    /** The VLC window exists (running, or open without control). */
    val vlcOpen: Boolean = false,
    /** Monitor with the VLC window now. */
    val currentId: String? = null,
    /** Monitor VLC opens on; `null` = not managed. */
    val preferredId: String? = null,
) {
    val current: Display? get() = items.find(currentId)
    val preferred: Display? get() = items.find(preferredId)

    /** Monitors are known (the list arrived): they can be chosen, also while VLC is closed, or left alone. */
    val canChoose: Boolean get() = items.isNotEmpty()

    /** The radio selection: the preferred monitor. */
    val selectedId: String? get() = preferredId

    val unmanagedSelected: Boolean get() = canChoose && preferredId == null

    /** A preferred monitor is remembered but not connected now (the TV is off). */
    val preferredMissing: Boolean get() = canChoose && preferredId != null && preferred == null

    /** Chip on the remote: the monitor with VLC, else the one VLC will open on; `null` = unknown / none. */
    val chipDisplay: Display? get() = current ?: preferred

    /** "Display N" for a monitor without a name: its place in the list. */
    fun numberOf(display: Display): Int = items.indexOf(display) + 1

    /** Command for a tap on [display]: move the window there (if open) and remember it. */
    fun commandFor(display: Display): SystemCommand = SystemCommand.MoveToDisplay(display.id)

    /** What to show right after `moveToDisplay` to [display] succeeded. */
    fun markAfterMove(display: Display, system: SystemState?): DisplayMark = DisplayMark(
        baseline = system,
        currentId = display.id.takeIf { vlcOpen },
        preferredSet = true,
        preferredId = display.id,
    )

    /** What to show right after `preferredDisplay {null}` succeeded. */
    fun markAfterUnmanage(system: SystemState?): DisplayMark =
        DisplayMark(baseline = system, preferredSet = true, preferredId = null)

    private fun List<Display>.find(id: String?): Display? = id?.let { i -> firstOrNull { it.id == i } }
}

fun displayChoice(
    snapshot: DisplaySnapshot?,
    system: SystemState?,
    vlcOpen: Boolean,
    mark: DisplayMark?,
): DisplayChoice {
    val list = snapshot?.list ?: DisplayList()
    val items = list.items
    val listFresh = snapshot != null && snapshot.systemAtLoad == system
    val activeMark = mark?.takeIf { it.baseline == system }

    val currentFromState = system?.displayId?.takeIf { it.isNotEmpty() }
    val currentFromList = items.firstOrNull { it.current == true }?.id
    val current = when {
        !vlcOpen -> null
        activeMark?.currentId != null -> activeMark.currentId
        listFresh && currentFromList != null -> currentFromList
        else -> currentFromState ?: currentFromList
    }

    val preferredFromList = list.preferredId?.takeIf { it.isNotEmpty() } ?: items.firstOrNull { it.preferred == true }?.id
    val preferred = when {
        activeMark?.preferredSet == true -> activeMark.preferredId
        listFresh || system == null -> preferredFromList
        else -> system.preferredDisplayId?.takeIf { it.isNotEmpty() }
    }

    return DisplayChoice(items = items, vlcOpen = vlcOpen, currentId = current, preferredId = preferred)
}
