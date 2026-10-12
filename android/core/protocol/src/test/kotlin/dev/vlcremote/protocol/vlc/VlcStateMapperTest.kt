package dev.vlcremote.protocol.vlc

import dev.vlcremote.protocol.model.PlaybackStatus
import dev.vlcremote.protocol.model.TrackKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class VlcStateMapperTest {

    private val playing = VlcStatusParser.parseStatus(fixture("status-playing.json"))
    private val stopped = VlcStatusParser.parseStatus(fixture("status-stopped.json"))
    private val page = VlcStatusParser.parseTracks(fixture("tracks-custom-page.json"))

    @Test
    fun `level 0 - tracks from streams, current remembered client-side`() {
        val remembered = RememberedTracks(audio = 2, subtitle = -1)
        val state = VlcStateMapper.map(
            status = playing, tracksPage = null, remembered = remembered, level = 0,
            artUrl = "http://pc:8080/art", muted = false, nowMs = 1_000,
        )
        assertEquals(PlaybackStatus.PLAYING, state.playback.state)
        assertEquals(6, state.tracks.audio.size)
        assertEquals(6, state.tracks.subtitle.size)
        assertEquals(1, state.tracks.video.size)
        assertEquals(2, state.tracks.currentAudio)
        assertEquals(-1, state.tracks.currentSubtitle)
        assertNull(state.tracks.currentVideo)
        assertEquals("Russian", state.tracks.audio[0].lang)
        assertEquals("Dub Мосфильм / AC3 5.1 @ 448 kbps", state.tracks.audio[0].name)
        assertEquals(40, state.chapters.count)
        assertEquals(2, state.chapters.index)
        assertNull(state.chapters.names)
        assertEquals(3, state.item.plId)
        assertEquals("http://pc:8080/art", state.item.artUrl)
        assertTrue(state.item.title!!.startsWith("The Lord of the Rings"))
        assertEquals(0, state.vlc.level)
        assertEquals(0, state.volume.value)
        assertEquals(1000L, state.playback.updatedAt)
    }

    @Test
    fun `level 1 - current tracks and chapter names from the lua page`() {
        val state = VlcStateMapper.map(
            status = playing, tracksPage = page, remembered = RememberedTracks.NONE, level = 1,
            artUrl = null, muted = false, nowMs = 0,
        )
        assertEquals(4, state.tracks.currentAudio)
        assertEquals(10, state.tracks.currentSubtitle)
        assertEquals(0, state.tracks.currentVideo)
        assertEquals(6, state.tracks.audio.size) // "Disable" entry is not a track
        assertEquals(listOf(1, 2, 3, 4, 5, 6), state.tracks.audio.map { it.id })
        assertEquals("English", state.tracks.audio.first { it.id == 4 }.lang)
        assertEquals(40, state.chapters.names!!.size)
        assertEquals("The Shire", state.chapters.names!![1])
        assertEquals(1, state.titles.count)
        assertEquals(1, state.vlc.level)
    }

    @Test
    fun `stopped keeps previous item and reports stopped`() {
        val before = VlcStateMapper.map(playing, page, RememberedTracks.NONE, 1, null, false, 0)
        val after = VlcStateMapper.map(stopped, null, RememberedTracks.NONE, 1, null, false, 5, previous = before)
        assertEquals(PlaybackStatus.STOPPED, after.playback.state)
        assertEquals(before.item, after.item)
        assertEquals(0, after.chapters.count)
        assertTrue(after.tracks.audio.isEmpty())
        assertEquals(266, after.volume.value)
    }

    @Test
    fun `interpolation advances time while playing and clamps to length`() {
        val state = VlcStateMapper.map(playing, null, RememberedTracks.NONE, 0, null, false, nowMs = 10_000)
        assertEquals(904.0, state.interpolatedTime(10_000), 1e-9)
        assertEquals(906.5, state.interpolatedTime(12_500), 1e-9)
        assertEquals(10705.0, state.interpolatedTime(100_000_000), 1e-9)
        val paused = state.copy(playback = state.playback.copy(state = PlaybackStatus.PAUSED))
        assertEquals(904.0, paused.interpolatedTime(99_999), 1e-9)
    }

    @Test
    fun `remembered tracks helper`() {
        val r = RememberedTracks.NONE.with(TrackKind.AUDIO, 3).with(TrackKind.SUBTITLE, -1)
        assertEquals(3, r.audio)
        assertEquals(-1, r.subtitle)
        assertNull(r.video)
    }
}
