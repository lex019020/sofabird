package dev.vlcremote.app.ui.remote

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.companion.BluetoothStatus
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.domain.model.AppSettings
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.protocol.companion.AudioDevice
import dev.vlcremote.protocol.companion.Display
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.backend.SystemCommand
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.TrackKind
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.model.VlcProcessStatus
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

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
    /** Companion `getAudioDevices` / `listDisplays`; loaded when the "More" sheet opens. */
    val audioDevices: List<AudioDevice> = emptyList(),
    val displays: List<Display> = emptyList(),
    val pcInfoLoading: Boolean = false,
    /** The active server is a companion (known before the first `welcome`). */
    val companionMode: Boolean = false,
    /** "Запустить VLC" / "Перезапустить" was pressed and VLC has not changed state yet. */
    val launching: Boolean = false,
    /** LAN is down; Bluetooth could take over after the user grants the permission / turns it on. */
    val bluetoothNeed: BluetoothStatus? = null,
) {
    val displayedPosition: Double get() = scrubTarget ?: position
    val isCompanion: Boolean get() = companionMode || state.vlc.level >= 2

    /** VLC state that replaces the remote with an explanation card; `null` = show the remote. */
    val vlcBlock: VlcProcessStatus? get() = vlcBlockingStatus(isCompanion, connection, state.vlc)
}

/**
 * Companion mode only: when VLC is not controllable ([VlcInfo.effectiveStatus] other than running)
 * the remote shows a state card instead of transport controls. Direct VLC mode never blocks — there
 * a dead VLC simply means no connection.
 */
fun vlcBlockingStatus(companion: Boolean, connection: ConnectionState, vlc: VlcInfo): VlcProcessStatus? {
    if (!companion || !connection.isOnline) return null
    return vlc.effectiveStatus.takeIf { it != VlcProcessStatus.RUNNING }
}

class RemoteViewModel(
    private val controller: PlayerController,
    settings: SettingsRepository,
) : ViewModel() {

    private val position = MutableStateFlow(0.0)
    private val scrubTarget = MutableStateFlow<Double?>(null)
    private val pcInfo = MutableStateFlow(PcInfo())
    private var pcInfoJob: Job? = null
    private val launching = MutableStateFlow(false)
    private var launchJob: Job? = null

    /** "Не сейчас" on the Bluetooth card: hidden while this screen lives. */
    private val bluetoothDismissed = MutableStateFlow(false)

    private data class PcInfo(
        val audioDevices: List<AudioDevice> = emptyList(),
        val displays: List<Display> = emptyList(),
        val loading: Boolean = false,
    )

    private data class Extras(
        val position: Double,
        val scrubTarget: Double?,
        val server: SavedServer?,
        val pcInfo: PcInfo,
        val launching: Boolean,
        val bluetoothNeed: BluetoothStatus?,
    )

    val uiState: StateFlow<RemoteUiState> = combine(
        controller.state,
        controller.connection,
        controller.capabilities,
        settings.flow,
        combine(
            combine(position, scrubTarget, controller.activeServer) { p, t, s -> Triple(p, t, s) },
            pcInfo,
            launching,
            combine(controller.bluetoothNeed, bluetoothDismissed) { need, dismissed -> need.takeUnless { dismissed } },
        ) { (p, t, s), i, l, bt -> Extras(p, t, s, i, l, bt) },
    ) { state, connection, caps, prefs, extras ->
        RemoteUiState(
            state = state,
            connection = connection,
            capabilities = caps,
            settings = prefs,
            position = extras.position,
            scrubTarget = extras.scrubTarget,
            hostName = extras.server?.displayName,
            audioDevices = extras.pcInfo.audioDevices,
            displays = extras.pcInfo.displays,
            pcInfoLoading = extras.pcInfo.loading,
            companionMode = extras.server?.isCompanion == true,
            launching = extras.launching,
            bluetoothNeed = extras.bluetoothNeed,
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
    /**
     * `cmd launch`: starts VLC, or (status `noControl`) restarts it with control. Shows progress until
     * the companion reports a new VLC state (at most [LAUNCH_WAIT_MS]).
     */
    fun launchVlc() {
        launchJob?.cancel()
        launchJob = viewModelScope.launch {
            launching.value = true
            try {
                val before = controller.state.value.vlc.effectiveStatus
                if (controller.sendAwait(PlayerCommand.Launch()).isFailure) return@launch
                withTimeoutOrNull(LAUNCH_WAIT_MS) {
                    controller.state.first { it.vlc.effectiveStatus != before }
                }
            } finally {
                launching.value = false
            }
        }
    }
    fun quitVlc() = send(PlayerCommand.Quit)

    // ---------------------------------------------------------------- Bluetooth fallback

    /** Permission granted / Bluetooth turned on: reconnect now (over RFCOMM if the LAN is still down). */
    fun bluetoothReady() = controller.retryNow()

    fun dismissBluetooth() {
        bluetoothDismissed.value = true
    }

    // ---------------------------------------------------------------- companion system controls

    /** Refresh audio devices / displays (only the lists the capabilities allow). */
    fun loadPcInfo() {
        val caps = controller.capabilities.value
        if (!caps.audioDevices && !caps.windowControl) return
        pcInfoJob?.cancel()
        pcInfoJob = viewModelScope.launch {
            pcInfo.update { it.copy(loading = true) }
            val devices = if (caps.audioDevices) controller.audioDevices().getOrNull() else null
            val displays = if (caps.windowControl) controller.listDisplays().getOrNull() else null
            pcInfo.update {
                it.copy(
                    audioDevices = devices ?: it.audioDevices,
                    displays = displays ?: it.displays,
                    loading = false,
                )
            }
        }
    }

    fun selectAudioDevice(id: String) {
        viewModelScope.launch {
            if (controller.sendSystemAwait(SystemCommand.AudioDevice(id)).isSuccess) {
                pcInfo.update { info ->
                    info.copy(audioDevices = info.audioDevices.map { d -> d.copy(current = d.id == id) })
                }
                loadPcInfo()
            }
        }
    }

    fun moveToDisplay(index: Int) = sendSystem(SystemCommand.MoveToDisplay(index))
    fun setKeepAwake(on: Boolean) = sendSystem(SystemCommand.KeepAwake(on))
    fun displayPower(on: Boolean) = sendSystem(SystemCommand.DisplayPower(on))
    fun sleepPc() = sendSystem(SystemCommand.Sleep)
    fun setSystemVolume(value: Int) = sendSystem(SystemCommand.SystemVolume(value = value.coerceIn(0, 100)))
    fun toggleSystemMute() = sendSystem(SystemCommand.SystemVolume(mute = controller.state.value.system?.muted != true))

    private companion object {
        const val LAUNCH_WAIT_MS = 20_000L
    }
}
