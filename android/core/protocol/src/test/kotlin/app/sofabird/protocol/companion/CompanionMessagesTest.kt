package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.TrackKind
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CompanionMessagesTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    @Test
    fun `hello encodes with t first-class and explicit nulls`() {
        val hello = CompanionMessage.Hello(device = DeviceInfo("Pixel 8", "uuid-1"), token = null, pairCode = "123456")
        val text = CompanionCodec.encode(hello)
        val obj = CompanionCodec.json.parseToJsonElement(text) as JsonObject
        assertEquals("hello", obj["t"]!!.jsonPrimitive.content)
        assertEquals(1, obj["v"]!!.jsonPrimitive.content.toInt())
        assertEquals("123456", obj["pairCode"]!!.jsonPrimitive.content)
        assertTrue(obj.containsKey("token"))
        assertEquals("Pixel 8", (obj["device"] as JsonObject)["name"]!!.jsonPrimitive.content)
        assertTrue(CompanionCodec.encodeLine(hello).endsWith("\n"))
    }

    @Test
    fun `welcome decodes caps and vlc info`() {
        val msg = known(
            """{"t":"welcome","v":1,"name":"HTPC","id":"guid-1","caps":["currentTrack","launchVlc","bogus"],"token":"abc","vlc":{"running":true,"version":"3.0.23","apiVersion":3,"level":2}}""",
        )
        val welcome = msg as CompanionMessage.Welcome
        assertEquals("HTPC", welcome.name)
        assertEquals("guid-1", welcome.pcId)
        assertEquals("abc", welcome.token)
        val caps = Capabilities.fromWire(welcome.caps)
        assertTrue(caps.currentTrack)
        assertTrue(caps.launchVlc)
        assertFalse(caps.power)
        assertEquals(2, welcome.vlc.level)
        assertTrue(welcome.vlc.running)
    }

    @Test
    fun `err and ok carry ids`() {
        val err = known("""{"t":"err","id":"7","code":"unauthorized","msg":"pairing required"}""") as CompanionMessage.Err
        assertEquals("7", err.id)
        assertEquals(ErrorCode.UNAUTHORIZED, err.errorCode)
        val ok = known("""{"t":"ok","id":"7"}""") as CompanionMessage.Ok
        assertEquals("7", ok.id)
        val unknownCode = known("""{"t":"err","code":"something_new"}""") as CompanionMessage.Err
        assertEquals(ErrorCode.UNKNOWN, unknownCode.errorCode)
    }

    @Test
    fun `state message decodes into PlayerState`() {
        val text = """
        {
          "t": "state",
          "vlc": {"running": true, "version": "3.0.23", "apiVersion": 3, "level": 2},
          "playback": {"state": "playing", "time": 904, "length": 10705, "position": 0.084, "rate": 1.0, "updatedAt": 1760280000123},
          "item": {"plId": 3, "uri": "file:///D:/shared/x.mkv", "title": "The Lord of the Rings", "artUrl": "http://pc:47555/art?plId=3"},
          "tracks": {
            "audio":    [{"id": 1, "name": "Dub Мосфильм / AC3 5.1", "lang": "Russian", "codec": "a52"}],
            "subtitle": [{"id": 7, "name": "Мосфильм / SRT", "lang": "Russian", "codec": "subt"}],
            "video":    [{"id": 0, "name": "...", "lang": null, "codec": "hevc"}],
            "currentAudio": 4, "currentSubtitle": -1, "currentVideo": 0
          },
          "chapters": {"index": 2, "count": 40, "names": ["Prologue", "..."]},
          "titles": {"index": 0, "count": 1},
          "volume": {"value": 256, "muted": false},
          "video": {"fullscreen": true, "aspect": "default"},
          "delays": {"audio": 0.0, "subtitle": 0.0},
          "flags": {"random": false, "loop": false, "repeat": false},
          "system": {"volume": 60, "muted": false, "audioDevice": "{0.0.0.00000000}.{guid}", "display": 1, "keepAwake": true}
        }
        """.trimIndent()
        val state = (known(text) as CompanionMessage.State).toPlayerState()
        assertEquals(PlaybackStatus.PLAYING, state.playback.state)
        assertEquals(904L, state.playback.time)
        assertEquals(1760280000123L, state.playback.updatedAt)
        assertEquals(3, state.item.plId)
        assertEquals(4, state.tracks.currentAudio)
        assertEquals(-1, state.tracks.currentSubtitle)
        assertNull(state.tracks.video[0].lang)
        assertEquals(40, state.chapters.count)
        assertEquals("Prologue", state.chapters.names!![0])
        assertTrue(state.video.fullscreen)
        assertEquals(60, state.system!!.volume)
        assertEquals(1, state.system!!.display)
        assertEquals(2, state.vlc.level)

        // Partial state (no tracks.current*, no system) is fine.
        val partial = (known("""{"t":"state","playback":{"state":"paused"}}""") as CompanionMessage.State).toPlayerState()
        assertEquals(PlaybackStatus.PAUSED, partial.playback.state)
        assertNull(partial.system)
        assertNull(partial.tracks.currentAudio)

        // Round-trip through the message form.
        val reencoded = CompanionCodec.encode(CompanionMessage.State.from(state))
        assertEquals(state, (known(reencoded) as CompanionMessage.State).toPlayerState())
    }

    @Test
    fun `entries playlist tracks displays audioDevices pong event beacon decode`() {
        val entries = known("""{"t":"entries","id":"3","uri":"file:///","items":[{"name":"C:\\","uri":"file:///C:/","type":"dir"},{"name":"a.mkv","uri":"file:///D:/a.mkv","type":"file","size":10,"modified":1}]}""") as CompanionMessage.Entries
        assertEquals(2, entries.items.size)
        assertTrue(entries.items[0].isDir)
        assertEquals(10L, entries.items[1].size)

        val playlist = known("""{"t":"playlist","id":"5","items":[{"plId":3,"name":"x","uri":"file:///x","durationSec":10,"current":true}]}""") as CompanionMessage.Playlist
        assertEquals(3, playlist.items[0].plId)
        assertTrue(playlist.items[0].current)

        val tracks = known("""{"t":"tracks","id":"6","audio":[{"id":1,"name":"a"}],"subtitle":[],"video":[],"currentAudio":1}""") as CompanionMessage.TracksMsg
        assertEquals(1, tracks.toTracks().currentAudio)

        val displays = known("""{"t":"displays","id":"9","items":[{"index":0,"name":"TV","primary":false,"bounds":{"x":1920,"y":0,"width":3840,"height":2160}}]}""") as CompanionMessage.Displays
        assertEquals(3840, displays.items[0].bounds!!.width)

        val devices = known("""{"t":"audioDevices","id":"8","items":[{"id":"","name":"Default","current":true}]}""") as CompanionMessage.AudioDevices
        assertTrue(devices.items[0].current)

        assertTrue(known("""{"t":"pong"}""") is CompanionMessage.Pong)
        assertEquals("vlcExited", (known("""{"t":"event","name":"vlcExited","extra":1}""") as CompanionMessage.Event).name)

        val beacon = known("""{"t":"beacon","name":"HTPC","id":"g","wsPort":47555,"protocol":1,"needsPairing":true}""") as CompanionMessage.Beacon
        assertEquals("HTPC", beacon.name)
        assertTrue(beacon.needsPairing)
    }

    @Test
    fun `unknown type is tolerated, blank frames ignored`() {
        val decoded = CompanionCodec.decode("""{"t":"futureThing","x":1}""")
        assertTrue(decoded is DecodedMessage.Unknown)
        assertEquals("futureThing", (decoded as DecodedMessage.Unknown).type)
        assertNull(CompanionCodec.decode("   \n"))
    }

    @Test
    fun `info endpoint decodes`() {
        val info = CompanionCodec.decodeInfo("""{"name":"HTPC","version":"0.1","protocol":1,"vlc":{"running":false},"wsPort":47555,"needsPairing":true}""")
        assertEquals("HTPC", info.name)
        assertTrue(info.needsPairing)
        assertFalse(info.vlc!!.running)
    }

    @Test
    fun `command mapping to cmd and sys`() {
        fun cmd(c: PlayerCommand) = CommandMapping.toCmd("1", c)
        fun JsonObject.str(k: String) = this[k]!!.jsonPrimitive.content

        assertEquals("play", cmd(PlayerCommand.Play()).name)
        assertTrue(cmd(PlayerCommand.Play()).args.isEmpty())
        assertEquals("3", cmd(PlayerCommand.Play(3)).args.str("plId"))
        assertEquals("seek", cmd(PlayerCommand.SeekBy(-10)).name)
        assertEquals("-10", cmd(PlayerCommand.SeekBy(-10)).args.str("delta"))
        assertEquals("904", cmd(PlayerCommand.SeekTo(904)).args.str("seconds"))
        assertEquals("512", cmd(PlayerCommand.SetVolume(999)).args.str("value"))
        assertEquals("true", cmd(PlayerCommand.Mute(true)).args.str("on"))
        val track = cmd(PlayerCommand.SelectTrack(TrackKind.SUBTITLE, -1))
        assertEquals("subtitle", track.args.str("kind"))
        assertEquals("-1", track.args.str("id"))
        val delay = cmd(PlayerCommand.Delay(TrackKind.SUBTITLE, -0.5))
        assertEquals("subtitle", delay.args.str("kind"))
        assertEquals("-0.5", delay.args.str("seconds"))
        assertTrue(cmd(PlayerCommand.Fullscreen()).args.isEmpty())
        assertEquals("false", cmd(PlayerCommand.Fullscreen(false)).args.str("on"))
        val open = cmd(PlayerCommand.Open("file:///D:/x.mkv", enqueue = true, subtitle = "file:///D:/x.srt"))
        assertEquals("open", open.name)
        assertEquals("true", open.args.str("enqueue"))
        assertEquals("file:///D:/x.srt", open.args.str("subtitle"))
        assertEquals("quit", cmd(PlayerCommand.Quit).name)
        assertEquals("1", cmd(PlayerCommand.Launch(display = 1)).args.str("display"))
        assertEquals("playlistRemove", cmd(PlayerCommand.PlaylistRemove(2)).name)
        assertEquals("togglePause", cmd(PlayerCommand.TogglePause).name)

        val text = CompanionCodec.encode(cmd(PlayerCommand.VolumeBy(13)))
        val obj = CompanionCodec.json.parseToJsonElement(text) as JsonObject
        assertEquals("cmd", obj.str("t"))
        assertEquals("1", obj.str("id"))
        assertEquals("volume", obj.str("name"))
        assertEquals("13", (obj["args"] as JsonObject).str("delta"))

        val sys = CommandMapping.toSys("2", SystemCommand.SystemVolume(delta = 5))
        assertEquals("systemVolume", sys.name)
        assertEquals("5", sys.args.str("delta"))
        assertEquals("moveToDisplay", CommandMapping.toSys("3", SystemCommand.MoveToDisplay(displayId = null, legacyIndex = 1)).name)
        assertEquals("", CommandMapping.toSys("4", SystemCommand.AudioDevice("")).args.str("id"))
        assertEquals("sleep", CommandMapping.toSys("5", SystemCommand.Sleep).name)
        assertEquals(JsonPrimitive(true), CommandMapping.toSys("6", SystemCommand.KeepAwake(true)).args["on"])
    }

    @Test
    fun `system request and response round-trips`() {
        fun JsonObject.str(k: String) = this[k]!!.jsonPrimitive.content
        fun obj(m: CompanionMessage) = CompanionCodec.json.parseToJsonElement(CompanionCodec.encode(m)) as JsonObject

        // getAudioDevices → audioDevices
        val getDevices = obj(CompanionMessage.GetAudioDevices("8"))
        assertEquals("getAudioDevices", getDevices.str("t"))
        assertEquals("8", getDevices.str("id"))
        val devices = known("""{"t":"audioDevices","id":"8","items":[{"id":"","name":"Default","current":false},{"id":"{0.0.0.00000000}.{guid}","name":"HDMI (TV)","current":true}]}""") as CompanionMessage.AudioDevices
        assertEquals("8", devices.id)
        assertEquals(2, devices.items.size)
        assertEquals("{0.0.0.00000000}.{guid}", devices.items.first { it.current }.id)
        assertEquals(devices, (known(CompanionCodec.encode(devices)) as CompanionMessage.AudioDevices))

        // sys listDisplays → displays
        val listDisplays = obj(CommandMapping.toSys("9", SystemCommand.ListDisplays))
        assertEquals("sys", listDisplays.str("t"))
        assertEquals("listDisplays", listDisplays.str("name"))
        assertTrue((listDisplays["args"] as JsonObject).isEmpty())
        val displays = known("""{"t":"displays","id":"9","items":[{"index":0,"name":"Laptop","primary":true,"bounds":{"x":0,"y":0,"width":1920,"height":1080}},{"index":1,"name":"TV","primary":false,"bounds":{"x":1920,"y":0,"width":3840,"height":2160}}]}""") as CompanionMessage.Displays
        assertEquals("9", displays.id)
        assertEquals(listOf(0, 1), displays.items.map { it.index })
        assertTrue(displays.items[0].primary)
        assertEquals(3840, displays.items[1].bounds!!.width)
        assertEquals(displays, known(CompanionCodec.encode(displays)) as CompanionMessage.Displays)

        // sys commands used by the "More" sheet
        val audio = obj(CommandMapping.toSys("10", SystemCommand.AudioDevice("{guid}")))
        assertEquals("audioDevice", audio.str("name"))
        assertEquals("{guid}", (audio["args"] as JsonObject).str("id"))
        val move = obj(CommandMapping.toSys("11", SystemCommand.MoveToDisplay(displayId = null, legacyIndex = 1)))
        assertEquals("moveToDisplay", move.str("name"))
        assertEquals("1", (move["args"] as JsonObject).str("display"))
        val awake = obj(CommandMapping.toSys("12", SystemCommand.KeepAwake(false)))
        assertEquals("false", (awake["args"] as JsonObject).str("on"))
        val power = obj(CommandMapping.toSys("13", SystemCommand.DisplayPower(false)))
        assertEquals("displayPower", power.str("name"))
        assertEquals("sleep", obj(CommandMapping.toSys("14", SystemCommand.Sleep)).str("name"))
        val vol = obj(CommandMapping.toSys("15", SystemCommand.SystemVolume(value = 150)))
        assertEquals("100", (vol["args"] as JsonObject).str("value")) // clamped
        val mute = obj(CommandMapping.toSys("16", SystemCommand.SystemVolume(mute = true)))
        assertEquals("true", (mute["args"] as JsonObject).str("mute"))
        assertNull((mute["args"] as JsonObject)["value"])
    }
}
