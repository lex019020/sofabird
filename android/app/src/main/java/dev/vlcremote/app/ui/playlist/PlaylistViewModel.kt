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
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class PlaylistUiState(
    val items: List<PlaylistItem> = emptyList(),
    val currentPlId: Int? = null,
    val loading: Boolean = false,
    val error: Throwable? = null,
    val online: Boolean = false,
    /** The "button vibration" setting. */
    val haptics: Boolean = true,
)

class PlaylistViewModel(private val controller: PlayerController, private val settings: SettingsRepository) :
    ViewModel(), PlaylistActions {

    private companion object {
        /** VLC applies pl_delete asynchronously: read the playlist again after a moment. */
        const val RELOAD_AFTER_REMOVE_MS = 400L
        const val RELOAD_AFTER_CHANGE_MS = 600L
    }

    /** The playlist as last read from VLC; one flow, changed with [update]. */
    private data class Local(
        val items: List<PlaylistItem> = emptyList(),
        val loading: Boolean = false,
        val error: Throwable? = null,
    )

    private val local = MutableStateFlow(Local())
    private var loadJob: Job? = null

    val uiState: StateFlow<PlaylistUiState> = combine(
        local,
        controller.state.map { it.item.plId }.distinctUntilChanged(),
        controller.connection.map { it.isOnline }.distinctUntilChanged(),
        settings.settings.map { it.haptics }.distinctUntilChanged(),
    ) { l, current, online, haptics ->
        PlaylistUiState(
            items = l.items.map { it.copy(current = it.plId == current || (current == null && it.current)) },
            currentPlId = current,
            loading = l.loading,
            error = l.error,
            online = online,
            haptics = haptics,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), PlaylistUiState())

    init {
        // Reload when the current item changes or the connection comes up.
        viewModelScope.launch {
            combine(
                controller.state.map { it.item.plId }.distinctUntilChanged(),
                controller.connection.map { it.isOnline }.distinctUntilChanged(),
            ) { _, online -> online }
                .collect { online -> if (online) load() else local.update { it.copy(items = emptyList()) } }
        }
    }

    override fun refresh() = load()

    override fun play(item: PlaylistItem) {
        controller.send(PlayerCommand.Play(item.plId))
        scheduleReload()
    }

    override fun remove(item: PlaylistItem) {
        local.update { l -> l.copy(items = l.items.filterNot { it.plId == item.plId }) } // optimistic
        viewModelScope.launch {
            controller.sendAwait(PlayerCommand.PlaylistRemove(item.plId))
            delay(RELOAD_AFTER_REMOVE_MS)
            load() // pl_delete is unreliable: re-read the truth
        }
    }

    override fun clear() {
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
            local.update { it.copy(loading = true) }
            val result = controller.playlist()
            local.update { l ->
                result.fold(
                    onSuccess = { Local(items = it) },
                    onFailure = { l.copy(loading = false, error = it) },
                )
            }
        }
    }
}
