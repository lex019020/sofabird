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
 * 3. live `state.system` (`displayId` / `display`, `preferredDisplayId`).
 *
 * Old companions report no monitor ids: no preferred monitor, no "don't manage", moves by index.
 */

/** Key of a monitor within one list: the stable id, or `#index` for old companions. */
val Display.key: String get() = id?.takeIf { it.isNotEmpty() } ?: "#$index"

/** `listDisplays` answer plus the `state.system` seen when it arrived (to tell which one is newer). */
data class DisplaySnapshot(val list: DisplayList, val systemAtLoad: SystemState?)

/**
 * Optimistic marking after an action succeeded. Applies while `state.system` still equals [baseline]
 * (the companion has not reported anything newer).
 */
data class DisplayMark(
    val baseline: SystemState?,
    /** Monitor the VLC window was moved to (`null` = the window was not moved). */
    val currentKey: String? = null,
    /** [preferredKey] is meaningful (`null` there = "don't manage"). */
    val preferredSet: Boolean = false,
    val preferredKey: String? = null,
)

data class DisplayChoice(
    val items: List<Display> = emptyList(),
    /** New companion: a preferred monitor exists and "don't manage" can be offered. */
    val supportsIds: Boolean = false,
    /** The VLC window exists (running, or open without control). */
    val vlcOpen: Boolean = false,
    /** Monitor with the VLC window now. */
    val currentKey: String? = null,
    /** Monitor VLC opens on ([supportsIds] only); `null` = not managed. */
    val preferredKey: String? = null,
) {
    val current: Display? get() = items.find(currentKey)
    val preferred: Display? get() = items.find(preferredKey)

    /** The radio selection: the preferred monitor, or (old companions) the one with VLC. */
    val selectedKey: String? get() = if (supportsIds) preferredKey else currentKey

    /** "Do not manage" is offered (only companions with monitor ids know it). */
    val canUnmanage: Boolean get() = supportsIds

    val unmanagedSelected: Boolean get() = supportsIds && preferredKey == null

    /** A preferred monitor is remembered but not connected now (the TV is off). */
    val preferredMissing: Boolean get() = supportsIds && preferredKey != null && preferred == null

    /** Old companions can only move an existing window. */
    val canChoose: Boolean get() = items.isNotEmpty() && (supportsIds || vlcOpen)

    /** Chip on the remote: the monitor with VLC, else the one VLC will open on; `null` = unknown / none. */
    val chipDisplay: Display? get() = current ?: preferred

    /** Command for a tap on [display]: move (and remember) by id, or by index for old companions. */
    fun commandFor(display: Display): SystemCommand =
        if (supportsIds && !display.id.isNullOrEmpty()) SystemCommand.MoveToDisplay(display.id, legacyIndex = null)
        else SystemCommand.MoveToDisplay(displayId = null, legacyIndex = display.index)

    /** What to show right after `moveToDisplay` to [display] succeeded. */
    fun markAfterMove(display: Display, system: SystemState?): DisplayMark = DisplayMark(
        baseline = system,
        currentKey = display.key.takeIf { vlcOpen },
        preferredSet = supportsIds,
        preferredKey = display.key.takeIf { supportsIds },
    )

    /** What to show right after `preferredDisplay {null}` succeeded. */
    fun markAfterUnmanage(system: SystemState?): DisplayMark =
        DisplayMark(baseline = system, preferredSet = true, preferredKey = null)

    private fun List<Display>.find(key: String?): Display? = key?.let { k -> firstOrNull { it.key == k } }
}

fun displayChoice(
    snapshot: DisplaySnapshot?,
    system: SystemState?,
    vlcOpen: Boolean,
    mark: DisplayMark?,
): DisplayChoice {
    val list = snapshot?.list ?: DisplayList()
    val items = list.items
    val supportsIds = list.supportsIds
    val listFresh = snapshot != null && snapshot.systemAtLoad == system
    val activeMark = mark?.takeIf { it.baseline == system }

    fun byIndex(index: Int?): String? = index?.let { i -> items.firstOrNull { it.index == i }?.key ?: "#$i" }

    val currentFromState = system?.displayId?.takeIf { it.isNotEmpty() } ?: byIndex(system?.display)
    val currentFromList = items.firstOrNull { it.current == true }?.key
    val current = when {
        !vlcOpen -> null
        activeMark?.currentKey != null -> activeMark.currentKey
        listFresh && currentFromList != null -> currentFromList
        else -> currentFromState ?: currentFromList
    }

    val preferredFromList = list.preferredId?.takeIf { it.isNotEmpty() } ?: items.firstOrNull { it.preferred == true }?.key
    val preferred = when {
        !supportsIds -> null
        activeMark?.preferredSet == true -> activeMark.preferredKey
        listFresh || system == null -> preferredFromList
        else -> system.preferredDisplayId?.takeIf { it.isNotEmpty() }
    }

    return DisplayChoice(
        items = items,
        supportsIds = supportsIds,
        vlcOpen = vlcOpen,
        currentKey = current,
        preferredKey = preferred,
    )
}
