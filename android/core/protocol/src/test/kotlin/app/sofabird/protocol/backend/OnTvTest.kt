package app.sofabird.protocol.backend

import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlayingItem
import app.sofabird.protocol.model.ResumeChoice
import app.sofabird.protocol.model.ResumePrompt
import app.sofabird.protocol.model.SystemState
import app.sofabird.protocol.model.YouTubeState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

class OnTvTest {
    private val vlc = PlayerState(
        playback = Playback(state = PlaybackStatus.PAUSED, time = 600, length = 5400),
        item = PlayingItem(plId = 3, title = "Movie.mkv"),
        system = SystemState(volume = 40, muted = false),
        resume = ResumePrompt(uri = "file:///D:/Movie.mkv", position = 600),
    )
    private val youtube = YouTubeState(
        open = true, playing = true, videoId = "aqz-KE-bpKQ", title = "Big Buck Bunny", channel = "Blender",
        time = 60.0, length = 634.6, updatedAt = 1_000L,
    )

    @Test
    fun `vlc stays itself without youtube or when the browser is closed`() {
        assertSame(vlc, vlc.onTv())
        val closed = vlc.copy(youtube = youtube.copy(open = false))
        assertSame(closed, closed.onTv())
        assertNull(closed.routeToYouTube(PlayerCommand.TogglePause, 0L))
    }

    @Test
    fun `youtube is what the tv shows while it is open`() {
        val tv = vlc.copy(youtube = youtube).onTv()
        assertEquals(PlaybackStatus.PLAYING, tv.playback.state)
        assertEquals(60L, tv.playback.time)
        assertEquals(634L, tv.playback.length)
        assertEquals(1_000L, tv.playback.updatedAt)
        assertEquals("Big Buck Bunny", tv.item.title)
        assertNull(tv.item.plId)
        assertEquals("https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg", tv.item.artUrl)
        assertEquals(40, tv.volume.percent) // the PC's volume
        assertNull(tv.resume) // VLC's question waits behind YouTube
        assertEquals(70.0, tv.interpolatedTime(11_000L), 0.001)
    }

    @Test
    fun `transport and volume go to youtube while it is open`() {
        val s = vlc.copy(youtube = youtube)
        fun route(c: PlayerCommand) = s.routeToYouTube(c, nowMs = 11_000L)

        assertEquals(SystemCommand.YouTube(YouTubeAction.TOGGLE), route(PlayerCommand.TogglePause))
        assertEquals(SystemCommand.YouTube(YouTubeAction.PLAY), route(PlayerCommand.Play()))
        assertEquals(SystemCommand.YouTube(YouTubeAction.PAUSE), route(PlayerCommand.Stop))
        assertEquals(SystemCommand.YouTube(YouTubeAction.NEXT), route(PlayerCommand.Next))
        assertEquals(SystemCommand.YouTube(YouTubeAction.PREVIOUS), route(PlayerCommand.Prev))
        assertEquals(SystemCommand.YouTube(YouTubeAction.SEEK, 100.0), route(PlayerCommand.SeekTo(100)))
        // Relative to the position now: 60 s reported 10 s ago, playing.
        assertEquals(SystemCommand.YouTube(YouTubeAction.SEEK, 60.0), route(PlayerCommand.SeekBy(-10)))
        assertEquals(SystemCommand.SystemVolume(value = 50), route(PlayerCommand.SetVolume(128)))
        assertEquals(SystemCommand.SystemVolume(delta = -5), route(PlayerCommand.VolumeBy(-13)))
        assertEquals(SystemCommand.SystemVolume(delta = 5), route(PlayerCommand.VolumeBy(13)))
        assertEquals(SystemCommand.SystemVolume(mute = true), route(PlayerCommand.Mute(true)))
        // VLC's own commands stay VLC's.
        assertNull(route(PlayerCommand.Play(plId = 3)))
        assertNull(route(PlayerCommand.Open("file:///D:/Movie.mkv")))
        assertNull(route(PlayerCommand.Resume(ResumeChoice.RESUME)))
    }
}
