package app.sofabird.ui.library

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.sofabird.data.player.PlayerController
import app.sofabird.data.servers.ServerRepository
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.HistoryChange
import app.sofabird.protocol.model.HistoryFilter
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.ui.common.displayNameFromUri
import app.sofabird.ui.history.CONTINUE_LIMIT
import app.sofabird.ui.history.ContinueCard
import app.sofabird.ui.history.continueCards
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

const val ROOT_URI = "file:///"

data class Crumb(val name: String, val uri: String)

data class LibraryUiState(
    val uri: String = ROOT_URI,
    val crumbs: List<Crumb> = emptyList(),
    val entries: List<Entry> = emptyList(),
    val loading: Boolean = false,
    val error: Throwable? = null,
    val mediaOnly: Boolean = true,
    val favorites: List<String> = emptyList(),
    val online: Boolean = false,
    /** Entry whose context menu is open (long press). */
    val menuFor: Entry? = null,
    /** Companion with `history`: "Continue watching" row and the "History" button. */
    val historyEnabled: Boolean = false,
    val continueCards: List<ContinueCard> = emptyList(),
    /** "Continue watching" card whose menu is open (long press). */
    val continueMenuFor: HistoryItem? = null,
    /** The "button vibration" setting. */
    val haptics: Boolean = true,
) {
    val isRoot: Boolean get() = uri == ROOT_URI
    val isFavorite: Boolean get() = uri in favorites
    val visibleEntries: List<Entry>
        get() = entries
            .filter { it.isDir || !mediaOnly || it.isMediaFile }
            .sortedWith(compareByDescending<Entry> { it.isDir }.thenBy { it.name.lowercase() })
}

class LibraryViewModel(
    private val controller: PlayerController,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
) : ViewModel(), LibraryActions {

    /** What this screen keeps besides the player and the settings; one flow, changed with [update]. */
    private data class Local(
        val uri: String = ROOT_URI,
        val entries: List<Entry> = emptyList(),
        val loading: Boolean = false,
        val error: Throwable? = null,
        val menuFor: Entry? = null,
        val continueItems: List<HistoryItem> = emptyList(),
        val continueMenuFor: HistoryItem? = null,
    )

    private val local = MutableStateFlow(Local())
    private var loadJob: Job? = null
    private var continueJob: Job? = null
    private var refreshJob: Job? = null
    private var lastRevision: Long? = null
    private var restoredFor: String? = null

    private val historyEnabled = combine(controller.capabilities, controller.connection) { caps, c -> caps.history && c.isOnline }
        .distinctUntilChanged()

    /** `state.history.revision`: the screen passes it to [onHistoryRevision] while it is shown. */
    val historyRevision: Flow<Long?> = controller.state.map { it.history?.revision }.distinctUntilChanged()

    val uiState: StateFlow<LibraryUiState> = combine(
        local,
        settings.settings,
        controller.activeServer,
        controller.connection,
        historyEnabled,
    ) { l, prefs, server, connection, history ->
        LibraryUiState(
            uri = l.uri,
            crumbs = crumbs(l.uri),
            entries = l.entries,
            loading = l.loading,
            error = l.error,
            mediaOnly = prefs.libraryMediaOnly,
            favorites = server?.favoriteFolders.orEmpty(),
            online = connection.isOnline,
            menuFor = l.menuFor,
            historyEnabled = history,
            continueCards = if (history) continueCards(l.continueItems) else emptyList(),
            continueMenuFor = l.continueMenuFor.takeIf { history },
            haptics = prefs.haptics,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), LibraryUiState())

    init {
        // Restore the last folder per server, reload when the connection comes (back) up.
        viewModelScope.launch {
            combine(controller.activeServer, controller.connection.map { it.isOnline }.distinctUntilChanged()) { s, online -> s to online }
                .collect { (server, online) ->
                    if (server == null) return@collect
                    if (restoredFor != server.id) {
                        restoredFor = server.id
                        local.update { it.copy(uri = server.lastBrowseUri ?: ROOT_URI) }
                    }
                    if (online) load()
                }
        }
        // "Continue watching": whenever a companion with a history comes (back) online.
        viewModelScope.launch {
            historyEnabled.collect { on -> if (on) loadContinue() else local.update { it.copy(continueItems = emptyList()) } }
        }
    }

    /**
     * `state.history.revision` as seen by the screen (only while it is shown): the history changed on
     * the PC, so refresh the row and the progress marks of the folder. Debounced: playback saves the
     * position every 10 s.
     */
    fun onHistoryRevision(revision: Long?) {
        val previous = lastRevision
        lastRevision = revision
        if (revision == null || previous == null || revision == previous) return
        refreshJob?.cancel()
        refreshJob = viewModelScope.launch {
            delay(HISTORY_REFRESH_DEBOUNCE_MS)
            loadContinue()
            load(silent = true)
        }
    }

    private fun loadContinue() {
        if (!controller.capabilities.value.history) return
        continueJob?.cancel()
        continueJob = viewModelScope.launch {
            val result = controller.history(HistoryFilter.CONTINUE, CONTINUE_LIMIT, 0)
            result.onSuccess { page -> local.update { it.copy(continueItems = page.items) } } // a failure keeps the last row
        }
    }

    override fun playHistory(item: HistoryItem) {
        controller.send(PlayerCommand.Open(item.uri))
    }

    override fun showContinueMenu(item: HistoryItem?) {
        local.update { it.copy(continueMenuFor = item) }
    }

    /** "Mark as watched": the card leaves the row at once; the PC confirms with a new revision. */
    override fun markWatched(item: HistoryItem) = changeHistory(item, HistoryChange.SetFinished(true))

    override fun removeFromHistory(item: HistoryItem) = changeHistory(item, HistoryChange.Remove)

    private fun changeHistory(item: HistoryItem, change: HistoryChange) {
        local.update { l -> l.copy(continueItems = l.continueItems.filter { it.uri != item.uri }) }
        viewModelScope.launch {
            if (controller.updateHistory(item.uri, change).isFailure) loadContinue()
            load(silent = true)
        }
    }

    override fun open(entryUri: String) = navigate(normalizeDir(entryUri))

    override fun up() {
        val current = local.value.uri
        if (current != ROOT_URI) navigate(parentDirUri(current))
    }

    override fun goRoot() = open(ROOT_URI)

    override fun reload() = load()

    private fun navigate(target: String) {
        local.update { it.copy(uri = target) }
        load()
        val server = controller.activeServer.value ?: return
        viewModelScope.launch { servers.update(server.id) { it.copy(lastBrowseUri = target) } }
    }

    override fun setMediaOnly(value: Boolean) {
        viewModelScope.launch { settings.update { it.copy(libraryMediaOnly = value) } }
    }

    override fun toggleFavorite() {
        val server = controller.activeServer.value ?: return
        val u = local.value.uri
        viewModelScope.launch {
            servers.update(server.id) { s ->
                s.copy(favoriteFolders = if (u in s.favoriteFolders) s.favoriteFolders - u else s.favoriteFolders + u)
            }
        }
    }

    override fun play(entry: Entry) {
        controller.send(PlayerCommand.Open(entry.uri, enqueue = false, subtitle = siblingSubtitle(entry)))
    }

    override fun enqueue(entry: Entry) {
        controller.send(PlayerCommand.Open(entry.uri, enqueue = true))
    }

    override fun showMenu(entry: Entry?) {
        local.update { it.copy(menuFor = entry) }
    }

    /** [silent]: a background refresh (the history changed); a failure then keeps the current list. */
    private fun load(silent: Boolean = false) {
        if (silent && loadJob?.isActive == true) return
        loadJob?.cancel()
        val target = local.value.uri
        loadJob = viewModelScope.launch {
            local.update { if (silent) it.copy(loading = true) else it.copy(loading = true, error = null) }
            val result = controller.browse(target)
            local.update { l ->
                when {
                    l.uri != target -> l
                    result.isSuccess -> l.copy(entries = result.getOrThrow(), error = null, loading = false)
                    !silent || l.entries.isEmpty() -> l.copy(error = result.exceptionOrNull(), loading = false)
                    else -> l.copy(loading = false)
                }
            }
        }
    }

    /** A subtitle file with the same base name next to the video is attached automatically. */
    private fun siblingSubtitle(entry: Entry): String? {
        val base = entry.name.substringBeforeLast('.')
        return local.value.entries.firstOrNull { e ->
            !e.isDir && e.extension in Entry.SUBTITLE_EXTENSIONS && e.name.substringBeforeLast('.').startsWith(base)
        }?.uri
    }

    private fun normalizeDir(u: String): String = if (u.endsWith("/")) u else "$u/"

    private fun crumbs(u: String): List<Crumb> =
        crumbUris(u).map { Crumb(displayNameFromUri(it), it) }

    private companion object {
        const val HISTORY_REFRESH_DEBOUNCE_MS = 1_500L
    }
}

/**
 * Parent folder of a directory URI. Local paths (`file:///D:/a/`) go up to the drive and then to [ROOT_URI];
 * network paths (`file://host/share/a/`) go up to the share root `file://host/share/` and then to [ROOT_URI]
 * (a bare `file://host/` is not a folder).
 */
internal fun parentDirUri(current: String): String {
    if (current == ROOT_URI) return ROOT_URI
    val crumbs = crumbUris(current)
    return if (crumbs.size <= 1) ROOT_URI else crumbs[crumbs.size - 2]
}

/** Folder chain from the top for a directory URI, e.g. `file:///D:/`, `file:///D:/a/`; UNC starts at `file://host/share/`. */
internal fun crumbUris(u: String): List<String> {
    if (u == ROOT_URI || !u.startsWith("file://")) return emptyList()
    val result = mutableListOf<String>()
    if (u.startsWith(ROOT_URI)) {
        var acc = ROOT_URI
        for (part in u.removePrefix(ROOT_URI).split('/').filter { it.isNotEmpty() }) {
            acc += "$part/"
            result += acc
        }
    } else {
        val parts = u.removePrefix("file://").split('/').filter { it.isNotEmpty() }
        if (parts.size < 2) return emptyList()
        var acc = "file://${parts[0]}/${parts[1]}/"
        result += acc
        for (part in parts.drop(2)) {
            acc += "$part/"
            result += acc
        }
    }
    return result
}
