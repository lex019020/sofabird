package app.sofabird.protocol.vlc

import app.sofabird.protocol.model.EntryType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class VlcStatusParserTest {

    @Test
    fun `parses playing status with streams and meta`() {
        val status = VlcStatusParser.parseStatus(fixture("status-playing.json"))

        assertEquals("playing", status.state)
        assertEquals(904L, status.time)
        assertEquals(10705L, status.length)
        assertEquals(3, status.currentPlId)
        assertEquals(0, status.volume)
        assertFalse(status.fullscreen)
        assertEquals("default", status.aspectRatio)
        assertEquals(3, status.apiVersion)
        assertEquals("3.0.23 Vetinari", status.version)
        assertEquals(10, status.seekSec)
        assertEquals(0.08447128534317, status.position, 1e-12)

        val info = status.information!!
        assertEquals(2, info.chapter)
        assertEquals(40, info.chapters.size)
        assertEquals(listOf(0), info.titles)
        assertEquals("The Lord of the Rings: The Fellowship of the Ring / 2001 / Warner Bros.", info.meta.title)
        assertTrue(info.meta.artworkUrl!!.startsWith("file:///C:/Users/"))
        assertTrue(info.meta.filename!!.endsWith(".mkv"))

        // 13 streams: 0 video, 1..6 audio, 7..12 subtitles; `meta` is not a stream.
        assertEquals(13, info.streams.size)
        assertEquals((0..12).toList(), info.streams.map { it.id })
        val audio = info.streams.filter { it.type == VlcStreamType.AUDIO }
        assertEquals(listOf(1, 2, 3, 4, 5, 6), audio.map { it.id })
        val subs = info.streams.filter { it.type == VlcStreamType.SUBTITLE }
        assertEquals(listOf(7, 8, 9, 10, 11, 12), subs.map { it.id })
        assertEquals(VlcStreamType.VIDEO, info.streams.first { it.id == 0 }.type)

        val mosfilm = info.streams.first { it.id == 1 }
        assertEquals("Russian", mosfilm.language)
        assertEquals("Dub Мосфильм / AC3 5.1 @ 448 kbps", mosfilm.description)
        assertEquals("A52 Audio (aka AC3) (a52 )", mosfilm.codec)
    }

    @Test
    fun `parses stopped status without information and numeric fullscreen`() {
        val status = VlcStatusParser.parseStatus(fixture("status-stopped.json"))

        assertEquals("stopped", status.state)
        assertNull(status.information)
        assertFalse(status.fullscreen)
        assertEquals(-1, status.currentPlId)
        assertEquals(266, status.volume)
        assertEquals(0L, status.length)
    }

    @Test
    fun `fullscreen as true boolean and equalizer as object are tolerated`() {
        val body = """{"state":"paused","fullscreen":true,"equalizer":{"preamp":0,"bands":{"band_0":0}},"time":5,"length":10}"""
        val status = VlcStatusParser.parseStatus(body)
        assertTrue(status.fullscreen)
        assertEquals("paused", status.state)
        assertEquals(5L, status.time)
    }

    @Test
    fun `localized category keys are detected by Type and Original_ID wins`() {
        val body = """
            {"state":"playing","information":{"chapter":0,"chapters":[0],"title":0,"titles":[0],
             "category":{
               "Поток 0":{"Type":"Видео","Codec":"hevc"},
               "Поток 1":{"Type":"Аудио","Language":"Русский","Original_ID":17},
               "Поток 2":{"Type":"Субтитры","Language":"English"},
               "meta":{"title":"X"},
               "Статистика":{"foo":"bar"}
             }}}
        """.trimIndent()
        val info = VlcStatusParser.parseStatus(body).information!!
        assertEquals(3, info.streams.size)
        assertEquals(VlcStreamType.VIDEO, info.streams[0].type)
        val audio = info.streams.first { it.type == VlcStreamType.AUDIO }
        assertEquals(17, audio.id)
        val sub = info.streams.first { it.type == VlcStreamType.SUBTITLE }
        assertEquals(2, sub.id)
        assertEquals("X", info.meta.title)
    }

    @Test
    fun `vlc4 chapters_count fallback`() {
        val body = """{"state":"playing","information":{"chapter":1,"chapters_count":5,"category":{}}}"""
        val info = VlcStatusParser.parseStatus(body).information!!
        assertEquals(5, info.chapters.size)
    }

    @Test
    fun `lua error page is reported as a typed error`() {
        val html = "<html><head><title>Error loading /requests/status.json</title></head><body>oops</body></html>"
        try {
            VlcStatusParser.parseStatus(html)
            fail("expected VlcLuaErrorException")
        } catch (e: VlcLuaErrorException) {
            assertEquals("/requests/status.json", e.detail)
        }
        assertTrue(VlcErrorPage.isErrorPage(html))
        assertFalse(VlcErrorPage.isErrorPage("""{"title":"Error loading"}"""))
    }

    @Test
    fun `empty body is a parse error`() {
        try {
            VlcStatusParser.parseStatus("   ")
            fail("expected VlcParseException")
        } catch (_: VlcParseException) {
        }
    }

    @Test
    fun `playlist tree is flattened to leaves under node 1`() {
        val items = VlcStatusParser.parsePlaylist(fixture("playlist.json"))
        assertEquals(1, items.size)
        val item = items[0]
        assertEquals(3, item.plId)
        assertEquals(10705L, item.durationSec)
        assertTrue(item.current)
        assertTrue(item.uri.startsWith("file:///D:/shared/"))
        assertTrue(item.name.startsWith("The Lord of the Rings"))
    }

    @Test
    fun `playlist ignores media library and nested nodes are flattened`() {
        val body = """
            {"type":"node","id":"0","children":[
              {"type":"node","id":"1","name":"Playlist","children":[
                 {"type":"leaf","id":"5","name":"a","uri":"file:///a","duration":1},
                 {"type":"node","id":"9","name":"folder","children":[
                    {"type":"leaf","id":"6","name":"b","uri":"file:///b","duration":-1}
                 ]}
              ]},
              {"type":"node","id":"2","name":"Media Library","children":[
                 {"type":"leaf","id":"7","name":"ml","uri":"file:///ml","duration":1}
              ]}
            ]}
        """.trimIndent()
        val items = VlcStatusParser.parsePlaylist(body)
        assertEquals(listOf(5, 6), items.map { it.plId })
        assertEquals(0L, items[1].durationSec)
        assertFalse(items[0].current)
    }

    @Test
    fun `browse skips dotdot and uses uri`() {
        val body = """
            {"element":[
              {"type":"dir","name":"..","path":"D:\\","uri":"file:///D:/"},
              {"type":"dir","name":"Фильмы","path":"D:\\shared\\Фильмы","uri":"file:///D:/shared/%D0%A4%D0%B8%D0%BB%D1%8C%D0%BC%D1%8B","size":0,"modification_time":1700000000},
              {"type":"file","name":"movie.mkv","path":"D:\\shared\\movie.mkv","uri":"file:///D:/shared/movie.mkv","size":123456789,"modification_time":1700000001},
              {"type":"unknown","name":"weird","uri":"file:///D:/shared/weird"}
            ]}
        """.trimIndent()
        val entries = VlcStatusParser.parseBrowse(body)
        assertEquals(3, entries.size)
        assertEquals(EntryType.DIR, entries[0].type)
        assertEquals("Фильмы", entries[0].name)
        assertEquals(EntryType.FILE, entries[1].type)
        assertEquals(123456789L, entries[1].size)
        assertEquals(1700000001L, entries[1].modified)
        assertTrue(entries[1].isMediaFile)
        assertEquals(EntryType.UNKNOWN, entries[2].type)
    }

    @Test
    fun `browse of drives root`() {
        val body = """{"element":[{"type":"dir","name":"C:\\","path":"C:\\","uri":"file:///C:/"},{"type":"dir","name":"D:\\","path":"D:\\","uri":"file:///D:/"}]}"""
        val entries = VlcStatusParser.parseBrowse(body)
        assertEquals(listOf("file:///C:/", "file:///D:/"), entries.map { it.uri })
    }

    @Test
    fun `tracks page parses lists and splits languages`() {
        val page = VlcStatusParser.parseTracks(fixture("tracks-custom-page.json"))

        val audio = page.audio!!
        assertEquals(4, audio.currentInt)
        assertEquals(7, audio.items.size) // "Disable" + 6
        assertEquals("-1", audio.items[0].id)

        val (name, lang) = VlcStatusParser.splitTrackName(audio.items[1].name)
        assertEquals("Dub Мосфильм / AC3 5.1 @ 448 kbps", name)
        assertEquals("Russian", lang)

        val spu = page.spu!!
        assertEquals(10, spu.currentInt)
        assertEquals(7, spu.items.size)

        val chapters = page.chapter!!
        assertEquals(2, chapters.currentInt)
        assertEquals(40, chapters.items.size)
        assertEquals("Prologue: One Ring to Rule Them All...", chapters.items[0].name.trim())

        val aspect = page.aspect!!
        assertEquals("", aspect.current)
        assertEquals("16:9", aspect.items[1].id)

        assertEquals("1", page.zoom!!.current)
        assertNotNull(page.deinterlace)
        assertNotNull(page.stereoMode)
    }

    @Test
    fun `tracks page with null lists`() {
        val page = VlcStatusParser.parseTracks("""{"sofabird":1,"audio":null,"spu":null,"chapter":null}""")
        assertNull(page.audio)
        assertNull(page.spu)
        assertNull(page.chapter)
    }

    @Test
    fun `splitTrackName without language`() {
        val (name, lang) = VlcStatusParser.splitTrackName("Track 1")
        assertEquals("Track 1", name)
        assertNull(lang)
        val (n2, l2) = VlcStatusParser.splitTrackName("Name - []")
        assertEquals("Name", n2)
        assertNull(l2)
    }
}

internal fun fixture(name: String): String {
    val stream = VlcStatusParserTest::class.java.classLoader!!.getResourceAsStream(name)
        ?: error("fixture $name not found")
    return stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
}
