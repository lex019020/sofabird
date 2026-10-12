package dev.vlcremote.protocol.vlc

import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.TrackKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class VlcCommandsTest {

    private val toggles = VlcCommands.CurrentToggles(fullscreen = false, random = false, loop = true, repeat = false, chapter = 4)

    @Test
    fun `relative values encode plus as percent 2B`() {
        assertEquals("command=seek&val=%2B10", VlcCommands.seekBy(10))
        assertEquals("command=seek&val=-30", VlcCommands.seekBy(-30))
        assertEquals("command=volume&val=%2B13", VlcCommands.volumeBy(13))
        assertEquals("command=volume&val=-13", VlcCommands.volumeBy(-13))
    }

    @Test
    fun `absolute values and clamping`() {
        assertEquals("command=seek&val=904", VlcCommands.seekTo(904))
        assertEquals("command=seek&val=50%25", VlcCommands.seekPercent(50))
        assertEquals("command=volume&val=512", VlcCommands.volume(9000))
        assertEquals("command=volume&val=0", VlcCommands.volume(-5))
    }

    @Test
    fun `numbers are formatted compactly`() {
        assertEquals("command=rate&val=1.5", VlcCommands.rate(1.5))
        assertEquals("command=rate&val=1", VlcCommands.rate(1.0))
        assertEquals("command=subdelay&val=-0.25", VlcCommands.subtitleDelay(-0.25))
        assertEquals("command=audiodelay&val=0.05", VlcCommands.audioDelay(0.05))
    }

    @Test
    fun `uris with spaces and unicode are percent-encoded`() {
        val q = VlcCommands.inPlay("file:///D:/shared/Фильм 1.mkv", "sub-file=D:\\s.srt")
        assertTrue(q.startsWith("command=in_play&input=file%3A%2F%2F%2FD%3A%2Fshared%2F%D0%A4"))
        assertTrue(q.contains("%201.mkv"))
        assertTrue(q.contains("&option=sub-file%3DD%3A%5Cs.srt"))
        assertEquals("uri=file%3A%2F%2F%2F", VlcCommands.browse("file:///"))
    }

    @Test
    fun `every documented command builds`() {
        assertEquals("command=pl_play", VlcCommands.play())
        assertEquals("command=pl_play&id=3", VlcCommands.play(3))
        assertEquals("command=pl_pause", VlcCommands.togglePause())
        assertEquals("command=pl_forcepause", VlcCommands.forcePause())
        assertEquals("command=pl_forceresume", VlcCommands.forceResume())
        assertEquals("command=pl_stop", VlcCommands.stop())
        assertEquals("command=pl_next", VlcCommands.next())
        assertEquals("command=pl_previous", VlcCommands.previous())
        assertEquals("command=pl_delete&id=3", VlcCommands.delete(3))
        assertEquals("command=pl_empty", VlcCommands.empty())
        assertEquals("command=pl_random", VlcCommands.toggleRandom())
        assertEquals("command=pl_loop", VlcCommands.toggleLoop())
        assertEquals("command=pl_repeat", VlcCommands.toggleRepeat())
        assertEquals("command=fullscreen", VlcCommands.toggleFullscreen())
        assertEquals("command=aspectratio&val=16%3A9", VlcCommands.aspectRatio("16:9"))
        assertEquals("command=title&val=0", VlcCommands.title(0))
        assertEquals("command=chapter&val=12", VlcCommands.chapter(12))
        assertEquals("command=audio_track&val=4", VlcCommands.audioTrack(4))
        assertEquals("command=video_track&val=0", VlcCommands.videoTrack(0))
        assertEquals("command=subtitle_track&val=-1", VlcCommands.subtitleTrack(-1))
        assertEquals("command=in_enqueue&input=file%3A%2F%2F%2FD%3A%2Fa.mkv", VlcCommands.inEnqueue("file:///D:/a.mkv"))
        assertEquals("command=addsubtitle&val=file%3A%2F%2F%2FD%3A%2Fa.srt", VlcCommands.addSubtitle("file:///D:/a.srt"))
        assertEquals("command=key&val=nav-up", VlcCommands.key("key-nav-up"))
        assertEquals("command=key&val=subtitle-toggle", VlcCommands.key("subtitle-toggle"))
        assertEquals("command=snapshot", VlcCommands.snapshot())
        assertEquals("command=in_play&input=vlc%3A%2F%2Fquit", VlcCommands.quit())
        assertEquals("obj=input&name=audio-es&val=5", VlcCommands.setVar("input", "audio-es", "5"))
    }

    @Test
    fun `forCommand maps typed commands and respects toggles`() {
        assertEquals(listOf("command=pl_forceresume"), VlcCommands.forCommand(PlayerCommand.Play(), toggles))
        assertEquals(listOf("command=pl_play&id=7"), VlcCommands.forCommand(PlayerCommand.Play(7), toggles))
        assertEquals(listOf("command=subtitle_track&val=-1"), VlcCommands.forCommand(PlayerCommand.SelectTrack(TrackKind.SUBTITLE, -1), toggles))
        assertEquals(listOf("command=chapter&val=5"), VlcCommands.forCommand(PlayerCommand.ChapterBy(1), toggles))
        assertEquals(listOf("command=chapter&val=0"), VlcCommands.forCommand(PlayerCommand.ChapterBy(-10), toggles))
        // loop already on → nothing to send; random off → toggle
        assertTrue(VlcCommands.forCommand(PlayerCommand.Loop(true), toggles).isEmpty())
        assertEquals(listOf("command=pl_random"), VlcCommands.forCommand(PlayerCommand.Random(true), toggles))
        assertEquals(listOf("command=fullscreen"), VlcCommands.forCommand(PlayerCommand.Fullscreen(true), toggles))
        assertTrue(VlcCommands.forCommand(PlayerCommand.Fullscreen(false), toggles).isEmpty())
        assertEquals(listOf("command=fullscreen"), VlcCommands.forCommand(PlayerCommand.Fullscreen(null), toggles))
        assertTrue(VlcCommands.forCommand(PlayerCommand.Mute(true), toggles).isEmpty())
        assertTrue(VlcCommands.forCommand(PlayerCommand.Launch(), toggles).isEmpty())
        val open = VlcCommands.forCommand(PlayerCommand.Open("file:///D:/a.mkv", subtitle = "D:\\a.srt"), toggles).single()
        assertTrue(open.startsWith("command=in_play&input="))
        assertTrue(open.contains("&option=sub-file%3D"))
        assertEquals(listOf("command=in_enqueue&input=file%3A%2F%2F%2FD%3A%2Fa.mkv"), VlcCommands.forCommand(PlayerCommand.Open("file:///D:/a.mkv", enqueue = true), toggles))
    }
}
