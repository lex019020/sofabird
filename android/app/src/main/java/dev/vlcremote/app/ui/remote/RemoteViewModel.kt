package dev.vlcremote.app.ui.remote

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.domain.model.AppSettings
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.backend.SystemCommand
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.TrackKind
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class RemoteUiState(
    val state: PlayerState = PlayerState.EMPTY,
    val connection: ConnectionState = ConnectionState.Disconnected,
    val capabilities: Capabilities = Capabilities.NONE,
    val settings: AppSettings = AppSettings(),
    /** Interpolated position, seconds. */
    val position: Double = 0.0,
    /** User is dragging the scrub bar: show the target instead of the live position. */
    val scrubTarget: Double? = null,
    val hostName: String? = null,
) {
    val displayedPosition: Double get() = scrubTarget ?: position
    val isCompanion: Boolean get() = state.vlc.level >= 2
}

class RemoteViewModel(
    private val controller: PlayerController,
    settings: SettingsRepository,
) : ViewModel() {

    private val position = MutableStateFlow(0.0)
    private val scrubTarget = MutableStateFlow<Double?>(null)

    val uiState: StateFlow<RemoteUiState> = combine(
        controller.state,
        controller.connection,
        controller.capabilities,
        settings.flow,
        combine(position, scrubTarget, controller.activeServer) { p, t, s -> Triple(p, t, s) },
    ) { state, connection, caps, prefs, (pos, target, server) ->
        RemoteUiState(
            state = state,
            connection = connection,
            capabilities = caps,
            settings = prefs,
            position = pos,
            scrubTarget = target,
            hostName = server?.displayName,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), RemoteUiState())

    init {
        // Position ticker: 4 Hz while playing, otherwise follow the reported time.
        viewModelScope.launch {
            while (isActive) {
                val s = controller.state.value
                position.value = s.interpolatedTime(System.currentTimeMillis())
                delay(if (s.isPlaying) 250 else 1_000)
            }
        }
    }

    fun send(command: PlayerCommand) = controller.send(command)

    fun sendSystem(command: SystemCommand) = controller.sendSystem(command)

    fun togglePause() {
        val s = controller.state.value
        send(if (s.isStopped) PlayerCommand.Play() else PlayerCommand.TogglePause)
    }

    fun seekBy(delta: Long) = send(PlayerCommand.SeekBy(delta))

    fun onScrub(seconds: Double) {
        scrubTarget.value = seconds
    }

    fun onScrubFinished() {
        val target = scrubTarget.value ?: return
        send(PlayerCommand.SeekTo(target.toLong()))
        viewModelScope.launch {
            delay(700) // keep the target until the clean status arrives
            if (scrubTarget.value == target) scrubTarget.value = null
        }
    }

    fun setVolume(value: Int) = send(PlayerCommand.SetVolume(value.coerceIn(0, 512)))

    fun toggleMute() {
        val v = controller.state.value.volume
        send(PlayerCommand.Mute(!(v.muted || v.value == 0)))
    }

    fun selectTrack(kind: TrackKind, id: Int) = send(PlayerCommand.SelectTrack(kind, id))

    fun adjustDelay(kind: TrackKind, deltaMs: Int) {
        val d = controller.state.value.delays
        val current = if (kind == TrackKind.AUDIO) d.audio else d.subtitle
        val next = Math.round((current + deltaMs / 1000.0) * 1000) / 1000.0
        send(PlayerCommand.Delay(kind, next))
    }

    fun resetDelay(kind: TrackKind) = send(PlayerCommand.Delay(kind, 0.0))

    fun chapter(index: Int) = send(PlayerCommand.Chapter(index))
    fun rate(value: Double) = send(PlayerCommand.Rate(value))
    fun aspect(value: String) = send(PlayerCommand.Aspect(value))
    fun toggleFullscreen() = send(PlayerCommand.Fullscreen(null))
    fun launchVlc() = send(PlayerCommand.Launch())
    fun quitVlc() = send(PlayerCommand.Quit)
}
