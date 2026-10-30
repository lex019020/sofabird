package app.sofabird.ui.touchpad

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.sofabird.data.player.PlayerController
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.domain.model.AppSettings
import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.ConnectionState
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

data class TouchpadUiState(
    val online: Boolean = false,
    /** The companion takes `input` (an older one does not). */
    val available: Boolean = false,
    val haptics: Boolean = true,
)

/** The full-screen touchpad: whether the PC can take its input, and the input itself ([PlayerController.input]). */
class TouchpadViewModel(private val controller: PlayerController, settings: SettingsRepository) : ViewModel() {

    val uiState: StateFlow<TouchpadUiState> = combine(controller.connection, controller.capabilities, settings.settings, ::state)
        .stateIn(
            viewModelScope,
            SharingStarted.WhileSubscribed(5_000),
            state(controller.connection.value, controller.capabilities.value, settings.settings.value),
        )

    fun input(input: PcInput) = controller.input(input)

    private fun state(connection: ConnectionState, caps: Capabilities, prefs: AppSettings) =
        TouchpadUiState(online = connection.isOnline, available = caps.input, haptics = prefs.haptics)
}
