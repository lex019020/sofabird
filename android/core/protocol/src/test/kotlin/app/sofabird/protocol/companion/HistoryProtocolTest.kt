package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.EntryType
import app.sofabird.protocol.model.HistoryFilter
import app.sofabird.protocol.model.HistoryInfo
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.ResumeChoice
import app.sofabird.protocol.model.ResumePrompt
import app.sofabird.protocol.vlc.VlcCommands
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Watch history, resume prompt and next-episode offer (protocol/README.md "Watch history and resume"). */
class HistoryProtocolTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    private fun obj(message: CompanionMessage): JsonObject =
        CompanionCodec.json.parseToJsonElement(CompanionCodec.encode(message)) as JsonObject

    @Test
    fun `history capability is parsed`() {
        assertTrue(Capabilities.fromWire(listOf("history")).history)
        assertFalse(Capabilities.fromWire(listOf("bluetooth")).history)
    }

    @Test
    fun `old state without history fields decodes with nulls`() {
        val state = known("""{"t":"state","vlc":{"running":true,"level":2},"playback":{"state":"playing","time":5,"length":60}}""")
            as CompanionMessage.State
        val s = state.toPlayerState()
        assertNull(s.resume)
        assertNull(s.next)
        assertNull(s.history)
    }

    @Test
    fun `state carries resume prompt, next episode and revision`() {
        val state = known(
            """{"t":"state","playback":{"state":"paused","time":0,"length":10705},
               "resume":{"uri":"file:///D:/shared/x.mkv","title":"The Lord of the Rings","position":5025,"length":10705,"pending":true},
               "next":{"uri":"file:///D:/shared/S01E02.mkv","title":"Episode 2"},
               "history":{"revision":42}}""",
        ) as CompanionMessage.State
        val s = state.toPlayerState()
        assertEquals(ResumePrompt("file:///D:/shared/x.mkv", "The Lord of the Rings", 5025, 10705, pending = true), s.resume)
        assertEquals(0.469, s.resume!!.progress, 0.001)
        assertEquals(NextEpisode("file:///D:/shared/S01E02.mkv", "Episode 2"), s.next)
        assertEquals(HistoryInfo(42), s.history)
    }

    @Test
    fun `explicit nulls clear the prompt and the offer`() {
        val s = (known("""{"t":"state","resume":null,"next":null,"history":{"revision":3}}""") as CompanionMessage.State).toPlayerState()
        assertNull(s.resume)
        assertNull(s.next)
        assertEquals(3L, s.history!!.revision)
    }

    @Test
    fun `state round-trips through the codec`() {
        val original = PlayerState(
            resume = ResumePrompt("file:///a.mkv", null, 600, 0, pending = true),
            next = NextEpisode("file:///b.mkv"),
            history = HistoryInfo(7),
        )
        val decoded = (known(CompanionCodec.encode(CompanionMessage.State.from(original))) as CompanionMessage.State).toPlayerState()
        assertEquals(original, decoded)
    }

    @Test
    fun `browse entries with and without history marks`() {
        val entries = known(
            """{"t":"entries","id":"3","uri":"file:///D:/shared/","items":[
                {"name":"old.mkv","uri":"file:///D:/shared/old.mkv","type":"file","size":10},
                {"name":"half.mkv","uri":"file:///D:/shared/half.mkv","type":"file","progress":0.5,"finished":false},
                {"name":"done.mkv","uri":"file:///D:/shared/done.mkv","type":"file","progress":1.0,"finished":true},
                {"name":"dir","uri":"file:///D:/shared/dir/","type":"dir","progress":null,"finished":null}]}""",
        ) as CompanionMessage.Entries
        val (old, half, done, dir) = entries.items
        assertNull(old.progress)
        assertNull(old.finished)
        assertEquals(0.5, half.progress!!, 1e-9)
        assertEquals(false, half.finished)
        assertEquals(true, done.finished)
        assertEquals(EntryType.DIR, dir.type)
        assertNull(dir.progress)
        // Re-encoding keeps the marks.
        val again = known(CompanionCodec.encode(entries)) as CompanionMessage.Entries
        assertEquals(entries.items, again.items)
        assertEquals(Entry("x", "file:///x", EntryType.FILE), Entry("x", "file:///x", EntryType.FILE, progress = null))
    }

    @Test
    fun `getHistory encodes filter and paging`() {
        val o = obj(CompanionMessage.GetHistory("9", HistoryFilter.CONTINUE.wire, limit = 20, offset = 40))
        assertEquals("getHistory", o["t"]!!.jsonPrimitive.content)
        assertEquals("9", o["id"]!!.jsonPrimitive.content)
        assertEquals("continue", o["filter"]!!.jsonPrimitive.content)
        assertEquals(20, o["limit"]!!.jsonPrimitive.content.toInt())
        assertEquals(40, o["offset"]!!.jsonPrimitive.content.toInt())
    }

    @Test
    fun `history answer decodes full and minimal items`() {
        val msg = known(
            """{"t":"history","id":"9","total":120,"items":[
                {"uri":"file:///D:/shared/x.mkv","title":"X","name":"x.mkv","folderUri":"file:///D:/shared/","position":5025,
                 "length":10705,"progress":0.47,"finished":false,"lastWatched":1760280000123,"playCount":3,
                 "artUrl":"http://pc:47555/art?uri=x","next":false},
                {"uri":"file:///D:/s/e2.mkv","title":"E2","position":0,"length":0,"finished":false,"lastWatched":1760280000000,"next":true},
                {"uri":"file:///D:/s/e1.mkv","title":"E1","position":0,"length":1500,"finished":true,"lastWatched":1,"future":"ignored"}]}""",
        ) as CompanionMessage.History
        assertEquals("9", msg.id)
        assertEquals(120, msg.total)
        val (x, e2, e1) = msg.items
        assertEquals(HistoryItem("file:///D:/shared/x.mkv", "X", "x.mkv", "file:///D:/shared/", 5025, 10705, 0.47, false, 1760280000123, 3, "http://pc:47555/art?uri=x"), x)
        assertTrue(e2.next)
        assertNull(e2.folderUri)
        assertEquals(0.0, e2.fraction, 0.0)
        assertTrue(e1.finished)
        assertEquals(0, e1.playCount)
        assertEquals(msg, known(CompanionCodec.encode(msg)))
    }

    @Test
    fun `youtube videos in the history`() {
        assertEquals("youtube", obj(CompanionMessage.GetHistory("9", HistoryFilter.YOUTUBE.wire))["filter"]!!.jsonPrimitive.content)
        val msg = known(
            """{"t":"history","id":"9","total":1,"items":[
                {"uri":"https://www.youtube.com/watch?v=aqz-KE-bpKQ","title":"Big Buck Bunny","position":125,"length":600,
                 "progress":0.21,"finished":false,"lastWatched":1761768000000,"playCount":1,
                 "artUrl":"https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg","kind":"youtube","channel":"Blender"}]}""",
        ) as CompanionMessage.History
        val video = msg.items.single()
        assertTrue(video.isYouTube)
        assertEquals("Blender", video.channel)
        assertNull(video.folderUri)
        assertFalse(HistoryItem("file:///x.mkv").isYouTube)
        assertEquals(msg, known(CompanionCodec.encode(msg)))
    }

    @Test
    fun `item fraction falls back to position over length`() {
        assertEquals(0.25, HistoryItem("u", position = 300, length = 1200).fraction, 1e-9)
        assertEquals(0.5, HistoryItem("u", position = 300, length = 1200, progress = 0.5).fraction, 1e-9)
        assertEquals(1.0, HistoryItem("u", progress = 1.7).fraction, 1e-9)
    }

    @Test
    fun `historyUpdate sends only the field in use`() {
        val watched = obj(CompanionMessage.HistoryUpdate("10", "file:///x.mkv", finished = true))
        assertEquals("historyUpdate", watched["t"]!!.jsonPrimitive.content)
        assertEquals("true", watched["finished"]!!.jsonPrimitive.content)
        assertFalse(watched.containsKey("remove"))
        val removed = obj(CompanionMessage.HistoryUpdate("11", "file:///x.mkv", remove = true))
        assertEquals("true", removed["remove"]!!.jsonPrimitive.content)
        assertFalse(removed.containsKey("finished"))
        assertEquals("file:///x.mkv", removed["uri"]!!.jsonPrimitive.content)
    }

    @Test
    fun `historyClear encodes`() {
        val o = obj(CompanionMessage.HistoryClear("12"))
        assertEquals("historyClear", o["t"]!!.jsonPrimitive.content)
        assertEquals("12", o["id"]!!.jsonPrimitive.content)
    }

    @Test
    fun `resume and dismissNext commands`() {
        val resume = CommandMapping.toCmd("1", PlayerCommand.Resume(ResumeChoice.RESUME))
        assertEquals("resume", resume.name)
        assertEquals("resume", resume.args["choice"]!!.jsonPrimitive.content)
        val restart = CommandMapping.toCmd("2", PlayerCommand.Resume(ResumeChoice.RESTART))
        assertEquals("restart", restart.args["choice"]!!.jsonPrimitive.content)
        val dismiss = CommandMapping.toCmd("3", PlayerCommand.DismissNext)
        assertEquals("dismissNext", dismiss.name)
        assertTrue(dismiss.args.isEmpty())
        // Direct VLC mode has nothing to send for them.
        assertTrue(VlcCommands.forCommand(PlayerCommand.DismissNext, VlcCommands.CurrentToggles()).isEmpty())
    }

    @Test
    fun `resumePrompt event with payload decodes`() {
        val event = known("""{"t":"event","name":"resumePrompt","uri":"file:///x.mkv","title":"X","position":600,"length":1200,"pending":true}""")
            as CompanionMessage.Event
        assertEquals("resumePrompt", event.name)
        assertEquals("nextEpisode", (known("""{"t":"event","name":"nextEpisode","uri":"file:///y.mkv"}""") as CompanionMessage.Event).name)
    }
}
