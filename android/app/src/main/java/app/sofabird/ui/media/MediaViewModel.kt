package app.sofabird.ui.media

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.sofabird.data.player.PlayerController
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.Library
import app.sofabird.protocol.model.LibraryMovie
import app.sofabird.protocol.model.LibraryShow
import app.sofabird.protocol.model.MediaFile
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

enum class MediaFilter { ALL, MOVIES, SHOWS }

data class MediaUiState(
    /** The companion has a library (`library` capability) and is online. */
    val available: Boolean = false,
    /** The posters (not the folders) are chosen. */
    val postersView: Boolean = true,
    val loading: Boolean = false,
    val error: Throwable? = null,
    /** The PC is scanning the folders or fetching metadata: the list will change. */
    val scanning: Boolean = false,
    /** A library arrived at least once (an empty one means no media folders or no videos in them). */
    val loaded: Boolean = false,
    val movies: List<LibraryMovie> = emptyList(),
    val shows: List<LibraryShow> = emptyList(),
    /** Media folders the PC cannot reach now: their movies and series are missing. */
    val missingFolders: List<String> = emptyList(),
    val filter: MediaFilter = MediaFilter.ALL,
    /** The movie whose card is open. */
    val openMovie: LibraryMovie? = null,
    val haptics: Boolean = true,
) {
    val isEmpty: Boolean get() = loaded && movies.isEmpty() && shows.isEmpty()
    val visibleMovies: List<LibraryMovie> get() = if (filter == MediaFilter.SHOWS) emptyList() else movies
    val visibleShows: List<LibraryShow> get() = if (filter == MediaFilter.MOVIES) emptyList() else shows
}

/** What the posters can do; no-op defaults for previews and screenshot tests. */
interface MediaActions {
    fun reload() {}
    fun setFilter(filter: MediaFilter) {}
    fun openMovie(movie: LibraryMovie?) {}
    fun play(file: MediaFile) {}
    fun setPostersView(on: Boolean) {}
}

/** The companion's library: loaded when it is available and again whenever `state.library.revision` changes. */
class MediaViewModel(
    private val controller: PlayerController,
    private val settings: SettingsRepository,
) : ViewModel(), MediaActions {

    private data class Local(
        val library: Library? = null,
        val loading: Boolean = false,
        val error: Throwable? = null,
        val filter: MediaFilter = MediaFilter.ALL,
        val openMovie: LibraryMovie? = null,
    )

    private val local = MutableStateFlow(Local())
    private var loadJob: Job? = null

    private val available = combine(controller.capabilities, controller.connection) { caps, c -> caps.library && c.isOnline }
        .distinctUntilChanged()

    val uiState: StateFlow<MediaUiState> = combine(
        local,
        available,
        controller.state.map { it.library }.distinctUntilChanged(),
        settings.settings,
    ) { l, ok, info, prefs ->
        MediaUiState(
            available = ok,
            postersView = prefs.libraryPosters,
            loading = l.loading,
            error = l.error,
            scanning = info?.scanning == true || l.library?.scanning == true,
            loaded = l.library != null,
            movies = l.library?.movies.orEmpty(),
            shows = l.library?.shows.orEmpty(),
            missingFolders = l.library?.missingFolders.orEmpty(),
            filter = l.filter,
            openMovie = l.openMovie,
            haptics = prefs.haptics,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), MediaUiState())

    init {
        viewModelScope.launch {
            combine(available, controller.state.map { it.library?.revision }.distinctUntilChanged(), ::Pair)
                .distinctUntilChanged()
                .collect { (ok, _) -> if (ok) load() }
        }
    }

    override fun reload() = load()

    override fun setFilter(filter: MediaFilter) = local.update { it.copy(filter = filter) }

    override fun openMovie(movie: LibraryMovie?) = local.update { it.copy(openMovie = movie) }

    override fun play(file: MediaFile) = controller.send(PlayerCommand.Open(file.uri))

    override fun setPostersView(on: Boolean) {
        viewModelScope.launch { settings.update { it.copy(libraryPosters = on) } }
    }

    private fun load() {
        loadJob?.cancel()
        loadJob = viewModelScope.launch {
            // Posters arrive in bursts while the PC fetches metadata: one request per burst.
            if (local.value.library != null) delay(RELOAD_DEBOUNCE_MS)
            local.update { it.copy(loading = true) }
            val result = controller.library()
            local.update { l ->
                result.fold(
                    onSuccess = { l.copy(library = it, loading = false, error = null) },
                    onFailure = { l.copy(loading = false, error = it) },
                )
            }
        }
    }

    private companion object {
        const val RELOAD_DEBOUNCE_MS = 500L
    }
}
