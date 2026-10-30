package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.SystemCommand
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Monitor choice for the VLC window: `listDisplays`, `moveToDisplay`, `preferredDisplay`, `state.system`. */
class DisplayProtocolTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    private fun sysArgs(command: SystemCommand): Pair<String, JsonObject> {
        val obj = CompanionCodec.json.parseToJsonElement(CompanionCodec.encode(CommandMapping.toSys("1", command))) as JsonObject
        return obj["name"]!!.jsonPrimitive.content to (obj["args"] as JsonObject)
    }

    @Test
    fun `displays answer decodes ids kinds flags and preferredId`() {
        val msg = known(
            """{"t":"displays","id":"7","preferredId":"\\\\?\\DISPLAY#GSM5B7F#5&1","items":[
              {"id":"\\\\?\\DISPLAY#BOE0A1F#4&2","name":"BOE panel","kind":"internal","primary":true,"current":false,"preferred":false,"bounds":{"x":0,"y":0,"width":1920,"height":1080}},
              {"id":"\\\\?\\DISPLAY#GSM5B7F#5&1","name":"LG TV SSCR2","kind":"external","primary":false,"current":true,"preferred":true,"bounds":{"x":1920,"y":0,"width":3840,"height":2160}}
            ]}""",
        ) as CompanionMessage.Displays
        val list = msg.toList()
        assertEquals("""\\?\DISPLAY#GSM5B7F#5&1""", list.preferredId)
        val (laptop, tv) = list.items
        assertEquals("""\\?\DISPLAY#BOE0A1F#4&2""", laptop.id)
        assertTrue(laptop.isInternal)
        assertFalse(laptop.isExternal)
        assertTrue(tv.isExternal)
        assertEquals("LG TV SSCR2", tv.name)
        assertEquals(true, tv.current)
        assertEquals(true, tv.preferred)
        assertEquals(false, laptop.current)
        assertEquals(msg, known(CompanionCodec.encode(msg)))
    }

    @Test
    fun `optional monitor fields may be absent`() {
        val msg = known("""{"t":"displays","id":"9","items":[{"id":"tv","name":"TV"}]}""") as CompanionMessage.Displays
        val list = msg.toList()
        assertNull(list.preferredId)
        val tv = list.items.single()
        assertNull(tv.kind)
        assertNull(tv.current)
        assertNull(tv.preferred)
        assertNull(tv.bounds)
        assertFalse(tv.primary)
        assertFalse(tv.isExternal || tv.isInternal)
    }

    @Test
    fun `unknown kind is kept and not treated as internal or external`() {
        val msg = known("""{"t":"displays","id":"1","items":[{"id":"x","kind":"projector"}]}""") as CompanionMessage.Displays
        val d = msg.items.single()
        assertEquals("projector", d.kind)
        assertFalse(d.isInternal || d.isExternal)
    }

    @Test
    fun `moveToDisplay sends the displayId only`() {
        val (name, args) = sysArgs(SystemCommand.MoveToDisplay("""\\?\DISPLAY#GSM5B7F#5&1"""))
        assertEquals("moveToDisplay", name)
        assertEquals("""\\?\DISPLAY#GSM5B7F#5&1""", args["displayId"]!!.jsonPrimitive.content)
        assertEquals(setOf("displayId"), args.keys)
    }

    @Test
    fun `preferredDisplay sends the id or an explicit null`() {
        val (name, args) = sysArgs(SystemCommand.PreferredDisplay("tv"))
        assertEquals("preferredDisplay", name)
        assertEquals("tv", args["displayId"]!!.jsonPrimitive.content)

        val (_, unmanaged) = sysArgs(SystemCommand.PreferredDisplay(null))
        assertTrue(unmanaged.containsKey("displayId"))
        assertEquals(JsonNull, unmanaged["displayId"])
    }

    @Test
    fun `state system carries displayId and preferredDisplayId`() {
        val state = (
            known(
                """{"t":"state","vlc":{"running":true,"status":"running","level":2},"system":{"displayId":"tv-id","preferredDisplayId":"tv-id","keepAwake":true}}""",
            ) as CompanionMessage.State
            ).toPlayerState()
        assertEquals("tv-id", state.system!!.displayId)
        assertEquals("tv-id", state.system!!.preferredDisplayId)

        val unmanaged = (
            known("""{"t":"state","vlc":{"running":false,"status":"notRunning","level":2},"system":{"keepAwake":false}}""") as CompanionMessage.State
            ).toPlayerState()
        assertNull(unmanaged.system!!.displayId)
        assertNull(unmanaged.system!!.preferredDisplayId)
    }
}
