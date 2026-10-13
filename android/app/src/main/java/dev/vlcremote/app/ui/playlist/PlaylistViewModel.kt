package dev.vlcremote.app.ui.playlist

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.PlaylistItem
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

data class PlaylistUiState(
    val items: List<PlaylistItem> = emptyList(),
    val currentPlId: Int? = null,
    val loading: Boolean = false,
    val error: Throwable? = null,
    val online: Boolean = false,
)

class PlaylistViewModel(private val controller: PlayerController, private val settings: SettingsRepository) : ViewModel() {

    private companion object {
        /** VLC applies pl_delete asynchronously: read the playlist again after a moment. */
        const val RELOAD_AFTER_REMOVE_MS = 400L
        const val RELOAD_AFTER_CHANGE_MS = 600L
    }

    private val items = MutableStateFlow<List<PlaylistItem>>(emptyList())
    private val loading = MutableStateFlow(false)
    private val error = MutableStateFlow<Throwable?>(null)
    private var loadJob: Job? = null

    /** The "button vibration" setting. */
    val haptics: StateFlow<Boolean> = settings.settings.map { it.haptics }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), settings.settings.value.haptics)

    val uiState: StateFlow<PlaylistUiState> = combine(
        items,
        controller.state.map { it.item.plId }.distinctUntilChanged(),
        loading,
        error,
        controller.connection.map { it.isOnline }.distinctUntilChanged(),
    ) { list, current, l, err, online ->
        PlaylistUiState(
            items = list.map { it.copy(current = it.plId == current || (current == null && it.current)) },
            currentPlId = current,
            loading = l,
            error = err,
            online = online,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), PlaylistUiState())

    init {
        // Reload when the current item changes or the connection comes up.
        viewModelScope.launch {
            combine(
                controller.state.map { it.item.plId }.distinctUntilChanged(),
                controller.connection.map { it.isOnline }.distinctUntilChanged(),
            ) { _, online -> online }
                .collect { online -> if (online) load() else items.value = emptyList() }
        }
    }

    fun refresh() = load()

    fun play(item: PlaylistItem) {
        controller.send(PlayerCommand.Play(item.plId))
        scheduleReload()
    }

    fun remove(item: PlaylistItem) {
        items.value = items.value.filterNot { it.plId == item.plId } // optimistic
        viewModelScope.launch {
            controller.sendAwait(PlayerCommand.PlaylistRemove(item.plId))
            delay(RELOAD_AFTER_REMOVE_MS)
            load() // pl_delete is unreliable: re-read the truth
        }
    }

    fun clear() {
        viewModelScope.launch {
            controller.sendAwait(PlayerCommand.PlaylistClear)
            load()
        }
    }

    private fun scheduleReload() {
        viewModelScope.launch {
            delay(RELOAD_AFTER_CHANGE_MS)
            load()
        }
    }

    private fun load() {
        loadJob?.cancel()
        loadJob = viewModelScope.launch {
            loading.value = true
            controller.playlist()
                .onSuccess { items.value = it; error.value = null }
                .onFailure { error.value = it }
            loading.value = false
        }
    }
}
