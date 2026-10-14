package app.sofabird.service

import android.os.Looper
import androidx.media3.common.C
import androidx.media3.common.DeviceInfo
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.SimpleBasePlayer
import androidx.media3.common.util.Util
import app.sofabird.data.player.PlayerController
import app.sofabird.domain.model.AppSettings
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.VlcVolume
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture

/**
 * A media3 [Player] that mirrors the VLC [PlayerState] and forwards transport commands to the PC.
 * Nothing plays locally: the device is reported as a remote playback device so volume keys on the
 * lock screen are routed to [handleSetDeviceVolume]/[handleIncreaseDeviceVolume].
 */
class RemotePlayer(
    private val controller: PlayerController,
    /** Read on every use: the seek steps can change in the settings while the service runs. */
    private val settings: () -> AppSettings,
) : SimpleBasePlayer(Looper.getMainLooper()) {

    private val seekBackMs: Long get() = settings().seekShortSec * 1000L
    private val seekForwardMs: Long get() = settings().seekLongSec * 1000L

    private var current: PlayerState = controller.state.value
    private var hostName: String = ""

    /** Called on the main thread whenever the VLC state changes. */
    fun update(state: PlayerState, hostName: String) {
        current = state
        this.hostName = hostName
        invalidateState()
    }

    override fun getState(): State {
        val s = current
        val playing = s.playback.state == PlaybackStatus.PLAYING
        val hasItem = s.item.title != null || s.item.plId != null
        val durationUs = if (s.playback.length > 0) Util.msToUs(s.playback.length * 1000) else C.TIME_UNSET

        val commands = Player.Commands.Builder()
            .addAll(
                Player.COMMAND_PLAY_PAUSE,
                Player.COMMAND_STOP,
                Player.COMMAND_SEEK_TO_NEXT,
                Player.COMMAND_SEEK_TO_PREVIOUS,
                Player.COMMAND_SEEK_BACK,
                Player.COMMAND_SEEK_FORWARD,
                Player.COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM,
                Player.COMMAND_GET_CURRENT_MEDIA_ITEM,
                Player.COMMAND_GET_METADATA,
                Player.COMMAND_GET_TIMELINE,
                Player.COMMAND_GET_DEVICE_VOLUME,
                Player.COMMAND_SET_DEVICE_VOLUME_WITH_FLAGS,
                Player.COMMAND_ADJUST_DEVICE_VOLUME_WITH_FLAGS,
            )
            .build()

        val builder = State.Builder()
            .setAvailableCommands(commands)
            .setPlayWhenReady(playing, Player.PLAY_WHEN_READY_CHANGE_REASON_REMOTE)
            .setPlaybackState(if (hasItem && !s.isStopped) Player.STATE_READY else if (hasItem) Player.STATE_ENDED else Player.STATE_IDLE)
            .setDeviceInfo(
                DeviceInfo.Builder(DeviceInfo.PLAYBACK_TYPE_REMOTE)
                    .setMinVolume(0)
                    .setMaxVolume(100)
                    .build(),
            )
            .setDeviceVolume(s.volume.percent.coerceIn(0, 100))
            .setIsDeviceMuted(s.volume.muted)
            .setSeekBackIncrementMs(seekBackMs)
            .setSeekForwardIncrementMs(seekForwardMs)

        if (hasItem) {
            val metadata = MediaMetadata.Builder()
                .setTitle(s.item.title ?: hostName)
                .setArtist(hostName)
                .apply { s.item.artUrl?.let { setArtworkUri(android.net.Uri.parse(it)) } }
                .build()
            val item = MediaItemData.Builder(MEDIA_UID)
                .setMediaItem(MediaItem.Builder().setMediaId(s.item.plId?.toString() ?: "current").setMediaMetadata(metadata).build())
                .setMediaMetadata(metadata)
                .setDurationUs(durationUs)
                .setIsSeekable(s.playback.length > 0)
                .build()
            builder
                .setPlaylist(listOf(item))
                .setCurrentMediaItemIndex(0)
                .setContentPositionMs { (s.interpolatedTime(System.currentTimeMillis()) * 1000).toLong() }
        }
        return builder.build()
    }

    override fun handleSetPlayWhenReady(playWhenReady: Boolean): ListenableFuture<*> {
        controller.send(if (playWhenReady) PlayerCommand.Play() else PlayerCommand.Pause)
        return Futures.immediateVoidFuture()
    }

    override fun handleStop(): ListenableFuture<*> {
        controller.send(PlayerCommand.Stop)
        return Futures.immediateVoidFuture()
    }

    override fun handleSeek(mediaItemIndex: Int, positionMs: Long, seekCommand: Int): ListenableFuture<*> {
        when (seekCommand) {
            Player.COMMAND_SEEK_TO_NEXT, Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM -> controller.send(PlayerCommand.Next)
            Player.COMMAND_SEEK_TO_PREVIOUS, Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM -> controller.send(PlayerCommand.Prev)
            Player.COMMAND_SEEK_BACK -> controller.send(PlayerCommand.SeekBy(-seekBackMs / 1000))
            Player.COMMAND_SEEK_FORWARD -> controller.send(PlayerCommand.SeekBy(seekForwardMs / 1000))
            else -> if (positionMs != C.TIME_UNSET) controller.send(PlayerCommand.SeekTo(positionMs / 1000))
        }
        return Futures.immediateVoidFuture()
    }

    override fun handleSetDeviceVolume(deviceVolume: Int, flags: Int): ListenableFuture<*> {
        controller.send(PlayerCommand.SetVolume(VlcVolume.fromPercent(deviceVolume)))
        return Futures.immediateVoidFuture()
    }

    override fun handleIncreaseDeviceVolume(flags: Int): ListenableFuture<*> {
        controller.adjustVolume(VOLUME_STEP)
        return Futures.immediateVoidFuture()
    }

    override fun handleDecreaseDeviceVolume(flags: Int): ListenableFuture<*> {
        controller.adjustVolume(-VOLUME_STEP)
        return Futures.immediateVoidFuture()
    }

    override fun handleSetDeviceMuted(muted: Boolean, flags: Int): ListenableFuture<*> {
        controller.send(PlayerCommand.Mute(muted))
        return Futures.immediateVoidFuture()
    }

    private companion object {
        const val MEDIA_UID = "vlc-current"
        const val VOLUME_STEP = 13 // ≈5 % of 256
    }
}
