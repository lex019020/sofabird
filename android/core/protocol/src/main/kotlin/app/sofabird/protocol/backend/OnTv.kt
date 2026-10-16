package app.sofabird.protocol.backend

import app.sofabird.protocol.model.Chapters
import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlayingItem
import app.sofabird.protocol.model.Titles
import app.sofabird.protocol.model.Tracks
import app.sofabird.protocol.model.VlcVolume
import app.sofabird.protocol.model.Volume
import kotlin.math.roundToInt

/**
 * The player the TV shows, for the phone's media notification, lock screen and volume keys: YouTube while it is open in
 * the TV browser (VLC is paused behind it; the PC's volume is the one YouTube plays at), otherwise VLC itself. The
 * [PlayerState.youtube] block stays, so callers can tell the two apart.
 */
fun PlayerState.onTv(): PlayerState {
    val yt = youtube?.takeIf { it.open } ?: return this
    val time = yt.time ?: 0.0
    val length = yt.length ?: 0.0
    return copy(
        playback = Playback(
            state = if (yt.playing) PlaybackStatus.PLAYING else PlaybackStatus.PAUSED,
            time = time.toLong(),
            length = length.toLong(),
            position = if (length > 0) time / length else 0.0,
            updatedAt = yt.updatedAt,
        ),
        item = PlayingItem(title = yt.title, artUrl = yt.thumbnailUrl),
        volume = system?.volume?.let { Volume(VlcVolume.fromPercent(it), system.muted == true) } ?: volume,
        tracks = Tracks(),
        chapters = Chapters(),
        titles = Titles(),
        resume = null,
        next = null,
    )
}

/**
 * A command in VLC's terms (the notification, lock screen, headset buttons, volume keys) for YouTube while it is open on
 * the TV: transport goes to the TV browser, volume to the PC's volume. `null` — YouTube is not open, or the command is
 * VLC's own (opening a file, a playlist item, tracks…).
 */
fun PlayerState.routeToYouTube(command: PlayerCommand, nowMs: Long): SystemCommand? {
    val yt = youtube?.takeIf { it.open } ?: return null
    return when (command) {
        PlayerCommand.TogglePause -> SystemCommand.YouTube(YouTubeAction.TOGGLE)
        is PlayerCommand.Play -> if (command.plId == null) SystemCommand.YouTube(YouTubeAction.PLAY) else null
        PlayerCommand.Pause, PlayerCommand.Stop -> SystemCommand.YouTube(YouTubeAction.PAUSE)
        PlayerCommand.Next -> SystemCommand.YouTube(YouTubeAction.NEXT)
        PlayerCommand.Prev -> SystemCommand.YouTube(YouTubeAction.PREVIOUS)
        is PlayerCommand.SeekTo -> SystemCommand.YouTube(YouTubeAction.SEEK, command.seconds.toDouble())
        is PlayerCommand.SeekBy -> SystemCommand.YouTube(YouTubeAction.SEEK, yt.interpolatedTime(nowMs) + command.delta)
        is PlayerCommand.SetVolume -> SystemCommand.SystemVolume(value = VlcVolume.toPercent(command.value).coerceIn(0, 100))
        is PlayerCommand.VolumeBy -> SystemCommand.SystemVolume(delta = (command.delta * 100.0 / VlcVolume.UNITY).roundToInt())
        is PlayerCommand.Mute -> SystemCommand.SystemVolume(mute = command.on)
        else -> null
    }
}
