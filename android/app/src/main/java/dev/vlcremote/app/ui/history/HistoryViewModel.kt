package dev.vlcremote.app.ui.history

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.HistoryChange
import dev.vlcremote.protocol.model.HistoryFilter
import dev.vlcremote.protocol.model.HistoryItem
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
import java.time.Clock
import java.time.temporal.WeekFields
import java.util.Locale

data class HistoryUiState(
    /** The companion keeps a history (`history` capability). */
    val available: Boolean = false,
    /** The link is up (while reconnecting the loaded list stays on screen). */
    val online: Boolean = false,
    val items: List<HistoryItem> = emptyList(),
    val groups: List<HistoryGroup> = emptyList(),
    val total: Int = 0,
    val loading: Boolean = false,
    val loadingMore: Boolean = false,
    val error: Throwable? = null,
    /** Row whose menu is open (long press / "⋮"). */
    val menuFor: HistoryItem? = null,
    /** Name of the PC, for "Clear the history on …?". */
    val serverName: String = "",
    /** The "button vibration" setting. */
    val haptics: Boolean = true,
) {
    val canLoadMore: Boolean get() = items.size < total && !loading && !loadingMore && error == null
}

/** History screen: the whole watch history, paged with `getHistory all`, refreshed on `state.history.revision`. */
class HistoryViewModel(
    private val controller: PlayerController,
    private val settings: SettingsRepository,
    private val clock: Clock = Clock.systemDefaultZone(),
) : ViewModel(), HistoryActions {

    private data class Data(
        val items: List<HistoryItem> = emptyList(),
        val total: Int = 0,
        val loading: Boolean = false,
        val loadingMore: Boolean = false,
        val error: Throwable? = null,
        val menuFor: HistoryItem? = null,
    )

    private val data = MutableStateFlow(Data())
    private var loadJob: Job? = null
    private var refreshJob: Job? = null
    private var lastRevision: Long? = null

    private val firstDayOfWeek = WeekFields.of(Locale.getDefault()).firstDayOfWeek

    /** `state.history.revision`: the screen passes it to [onRevision] while it is shown. */
    val historyRevision: Flow<Long?> = controller.state.map { it.history?.revision }.distinctUntilChanged()

    val uiState: StateFlow<HistoryUiState> = combine(
        data,
        controller.capabilities,
        controller.connection,
        controller.activeServer,
        settings.settings,
    ) { d, caps, connection, server, prefs ->
        HistoryUiState(
            available = caps.history,
            online = connection.isOnline,
            items = d.items,
            groups = groupHistoryByDate(d.items, clock, firstDayOfWeek),
            total = d.total,
            loading = d.loading,
            loadingMore = d.loadingMore,
            error = d.error,
            menuFor = d.menuFor,
            serverName = server?.displayName.orEmpty(),
            haptics = prefs.haptics,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), HistoryUiState())

    init {
        viewModelScope.launch {
            combine(controller.capabilities, controller.connection) { caps, c -> caps.history && c.isOnline }
                .distinctUntilChanged()
                .collect { ready -> if (ready) reload() }
        }
    }

    /** First page again (pull to refresh, retry, history changed). Keeps as many rows as are loaded. */
    override fun reload() {
        val keep = data.value.items.size.coerceIn(PAGE, MAX_RELOAD)
        loadJob?.cancel()
        loadJob = viewModelScope.launch {
            data.update { it.copy(loading = true, error = null) }
            val result = controller.history(HistoryFilter.ALL, keep, 0)
            data.update { d ->
                result.fold(
                    onSuccess = { page -> Data(items = page.items.distinctBy { it.uri }, total = page.total) },
                    // A failed silent refresh keeps the list; only an empty screen shows the error.
                    onFailure = { e -> d.copy(loading = false, error = e.takeIf { d.items.isEmpty() }) },
                )
            }
        }
    }

    override fun loadMore() {
        val d = data.value
        val busy = d.loading || d.loadingMore || loadJob?.isActive == true
        if (d.items.size >= d.total || busy) return
        loadJob = viewModelScope.launch {
            data.update { it.copy(loadingMore = true) }
            val result = controller.history(HistoryFilter.ALL, PAGE, d.items.size)
            data.update { cur ->
                result.fold(
                    onSuccess = { page ->
                        val known = cur.items.mapTo(HashSet()) { it.uri }
                        cur.copy(items = cur.items + page.items.filter { it.uri !in known }, total = page.total, loadingMore = false)
                    },
                    onFailure = { cur.copy(loadingMore = false) },
                )
            }
        }
    }

    /** `state.history.revision` as seen by the screen; a change reloads the list (debounced). */
    fun onRevision(revision: Long?) {
        val previous = lastRevision
        lastRevision = revision
        if (revision == null || previous == null || revision == previous) return
        refreshJob?.cancel()
        refreshJob = viewModelScope.launch {
            delay(REFRESH_DEBOUNCE_MS)
            reload()
        }
    }

    override fun showMenu(item: HistoryItem?) {
        data.update { it.copy(menuFor = item) }
    }

    override fun play(item: HistoryItem) = controller.send(PlayerCommand.Open(item.uri))

    override fun setFinished(item: HistoryItem, finished: Boolean) {
        data.update { d ->
            d.copy(
                items = d.items.map {
                    if (it.uri != item.uri) it
                    else it.copy(finished = finished, position = 0, progress = if (finished) 1.0 else 0.0)
                },
            )
        }
        viewModelScope.launch {
            if (controller.updateHistory(item.uri, HistoryChange.SetFinished(finished)).isFailure) reload()
        }
    }

    override fun remove(item: HistoryItem) {
        data.update { d -> d.copy(items = d.items.filter { it.uri != item.uri }, total = (d.total - 1).coerceAtLeast(0)) }
        viewModelScope.launch {
            if (controller.updateHistory(item.uri, HistoryChange.Remove).isFailure) reload()
        }
    }

    override fun clearAll() {
        data.value = Data()
        viewModelScope.launch {
            if (controller.clearHistory().isFailure) reload()
        }
    }

    private companion object {
        const val PAGE = 50
        const val MAX_RELOAD = 500
        const val REFRESH_DEBOUNCE_MS = 1_000L
    }
}
