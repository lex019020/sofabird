package dev.vlcremote.app.ui.library

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.ui.common.displayNameFromUri
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.Entry
import kotlinx.coroutines.Job
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
) : ViewModel() {

    private val uri = MutableStateFlow(ROOT_URI)
    private val entries = MutableStateFlow<List<Entry>>(emptyList())
    private val loading = MutableStateFlow(false)
    private val error = MutableStateFlow<Throwable?>(null)
    private val menuFor = MutableStateFlow<Entry?>(null)
    private var loadJob: Job? = null
    private var restoredFor: String? = null

    val uiState: StateFlow<LibraryUiState> = combine(
        combine(uri, entries, loading, error) { u, e, l, err -> Quad(u, e, l, err) },
        settings.flow.map { it.libraryMediaOnly },
        controller.activeServer,
        controller.connection,
        menuFor,
    ) { (u, e, l, err), mediaOnly, server, connection, menu ->
        LibraryUiState(
            uri = u,
            crumbs = crumbs(u),
            entries = e,
            loading = l,
            error = err,
            mediaOnly = mediaOnly,
            favorites = server?.favoriteFolders.orEmpty(),
            online = connection.isOnline,
            menuFor = menu,
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
                        uri.value = server.lastBrowseUri ?: ROOT_URI
                    }
                    if (online) load()
                }
        }
    }

    fun open(entryUri: String) {
        uri.value = normalizeDir(entryUri)
        load()
        rememberPath()
    }

    fun up() {
        val current = uri.value
        if (current == ROOT_URI) return
        uri.value = parentDirUri(current)
        load()
        rememberPath()
    }

    fun goRoot() = open(ROOT_URI)

    fun reload() = load()

    fun setMediaOnly(value: Boolean) {
        viewModelScope.launch { settings.update { it.copy(libraryMediaOnly = value) } }
    }

    fun toggleFavorite() {
        val server = controller.activeServer.value ?: return
        val u = uri.value
        viewModelScope.launch {
            servers.update(server.id) { s ->
                s.copy(favoriteFolders = if (u in s.favoriteFolders) s.favoriteFolders - u else s.favoriteFolders + u)
            }
        }
    }

    fun play(entry: Entry) {
        controller.send(PlayerCommand.Open(entry.uri, enqueue = false, subtitle = siblingSubtitle(entry)))
    }

    fun enqueue(entry: Entry) {
        controller.send(PlayerCommand.Open(entry.uri, enqueue = true))
    }

    fun showMenu(entry: Entry?) {
        menuFor.value = entry
    }

    private fun load() {
        loadJob?.cancel()
        val target = uri.value
        loadJob = viewModelScope.launch {
            loading.value = true
            error.value = null
            val result = controller.browse(target)
            if (uri.value != target) return@launch
            result.onSuccess { entries.value = it }
                .onFailure { error.value = it }
            loading.value = false
        }
    }

    private fun rememberPath() {
        val server = controller.activeServer.value ?: return
        val u = uri.value
        viewModelScope.launch { servers.update(server.id) { it.copy(lastBrowseUri = u) } }
    }

    /** A subtitle file with the same base name next to the video is attached automatically. */
    private fun siblingSubtitle(entry: Entry): String? {
        val base = entry.name.substringBeforeLast('.')
        return entries.value.firstOrNull { e ->
            !e.isDir && e.extension in Entry.SUBTITLE_EXTENSIONS && e.name.substringBeforeLast('.').startsWith(base)
        }?.uri
    }

    private fun normalizeDir(u: String): String = if (u.endsWith("/")) u else "$u/"

    private fun crumbs(u: String): List<Crumb> =
        crumbUris(u).map { Crumb(displayNameFromUri(it), it) }

    private data class Quad<A, B, C, D>(val a: A, val b: B, val c: C, val d: D)
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
