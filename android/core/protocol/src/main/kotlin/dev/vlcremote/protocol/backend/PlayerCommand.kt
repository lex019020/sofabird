package dev.vlcremote.protocol.backend

import dev.vlcremote.protocol.model.ResumeChoice
import dev.vlcremote.protocol.model.TrackKind

/** Player commands, transport-agnostic. See protocol/README.md "Player commands: `cmd`". */
sealed class PlayerCommand {
    /** Resume, or start the given playlist item. */
    data class Play(val plId: Int? = null) : PlayerCommand()
    data object Pause : PlayerCommand()
    data object TogglePause : PlayerCommand()
    data object Stop : PlayerCommand()
    data object Next : PlayerCommand()
    data object Prev : PlayerCommand()

    /** Absolute seek, seconds. */
    data class SeekTo(val seconds: Long) : PlayerCommand()

    /** Relative seek, seconds (negative = backwards). */
    data class SeekBy(val delta: Long) : PlayerCommand()

    /** VLC volume 0..512. */
    data class SetVolume(val value: Int) : PlayerCommand()
    data class VolumeBy(val delta: Int) : PlayerCommand()
    data class Mute(val on: Boolean) : PlayerCommand()

    data class Rate(val value: Double) : PlayerCommand()

    /** `id = -1` disables the track. */
    data class SelectTrack(val kind: TrackKind, val id: Int) : PlayerCommand()

    data class Chapter(val index: Int) : PlayerCommand()
    data class ChapterBy(val delta: Int) : PlayerCommand()
    data class Title(val index: Int) : PlayerCommand()

    /** Audio/subtitle delay in seconds. */
    data class Delay(val kind: TrackKind, val seconds: Double) : PlayerCommand()

    /** `on = null` toggles. */
    data class Fullscreen(val on: Boolean? = null) : PlayerCommand()
    data class Aspect(val value: String) : PlayerCommand()

    data class Open(
        val uri: String,
        val enqueue: Boolean = false,
        val subtitle: String? = null,
        val audioTrack: Int? = null,
        val subtitleTrack: Int? = null,
    ) : PlayerCommand()

    data class PlaylistRemove(val plId: Int) : PlayerCommand()
    data object PlaylistClear : PlayerCommand()

    data class Random(val on: Boolean) : PlayerCommand()
    data class Loop(val on: Boolean) : PlayerCommand()
    data class Repeat(val on: Boolean) : PlayerCommand()

    /** Companion only: start VLC (optionally with a file and on a display). */
    data class Launch(val uri: String? = null, val display: Int? = null) : PlayerCommand()
    data object Quit : PlayerCommand()

    /** Companion `history`: answer the resume prompt (`state.resume`). */
    data class Resume(val choice: ResumeChoice) : PlayerCommand()

    /** Companion `history`: hide the next-episode offer (`state.next`). */
    data object DismissNext : PlayerCommand()
}

/** System-level commands; each needs the corresponding capability (companion only). */
sealed class SystemCommand {
    /**
     * Move the VLC window to a monitor and remember it as the preferred one (when VLC is closed the
     * companion only remembers it). [displayId] is the stable `id` from `listDisplays`; companions that
     * report no ids only understand [legacyIndex] (`{"display": int}`).
     */
    data class MoveToDisplay(val displayId: String?, val legacyIndex: Int? = null) : SystemCommand() {
        init {
            require(displayId != null || legacyIndex != null) { "displayId or legacyIndex is required" }
        }
    }

    /** Only remember the monitor VLC opens on; `null` = the companion does not manage the VLC window. */
    data class PreferredDisplay(val displayId: String?) : SystemCommand()
    data object ListDisplays : SystemCommand()
    data class KeepAwake(val on: Boolean) : SystemCommand()
    data class DisplayPower(val on: Boolean) : SystemCommand()
    data object Sleep : SystemCommand()
    data object Shutdown : SystemCommand()

    /** `""` = default device. */
    data class AudioDevice(val id: String) : SystemCommand()
    data class SystemVolume(
        val value: Int? = null,
        val delta: Int? = null,
        val mute: Boolean? = null,
    ) : SystemCommand()
}
