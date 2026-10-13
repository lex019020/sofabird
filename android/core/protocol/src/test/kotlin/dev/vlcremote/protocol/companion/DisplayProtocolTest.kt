package dev.vlcremote.protocol.companion

import dev.vlcremote.protocol.backend.SystemCommand
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
    fun `new displays answer decodes ids kinds flags and preferredId`() {
        val msg = known(
            """{"t":"displays","id":"7","preferredId":"\\\\?\\DISPLAY#GSM5B7F#5&1","items":[
              {"index":0,"id":"\\\\?\\DISPLAY#BOE0A1F#4&2","name":"BOE panel","kind":"internal","primary":true,"current":false,"preferred":false,"bounds":{"x":0,"y":0,"width":1920,"height":1080}},
              {"index":1,"id":"\\\\?\\DISPLAY#GSM5B7F#5&1","name":"LG TV SSCR2","kind":"external","primary":false,"current":true,"preferred":true,"bounds":{"x":1920,"y":0,"width":3840,"height":2160}}
            ]}""",
        ) as CompanionMessage.Displays
        val list = msg.toList()
        assertTrue(list.supportsIds)
        assertEquals("""\\?\DISPLAY#GSM5B7F#5&1""", list.preferredId)
        val (laptop, tv) = list.items
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
    fun `old displays answer without ids still decodes`() {
        val msg = known(
            """{"t":"displays","id":"9","items":[{"index":0,"name":"Laptop","primary":true,"bounds":{"x":0,"y":0,"width":1920,"height":1080}},{"index":1,"name":"TV","primary":false}]}""",
        ) as CompanionMessage.Displays
        val list = msg.toList()
        assertFalse(list.supportsIds)
        assertNull(list.preferredId)
        assertNull(list.items[1].id)
        assertNull(list.items[1].kind)
        assertNull(list.items[1].current)
        assertNull(list.items[1].preferred)
        assertFalse(list.items[1].isExternal || list.items[1].isInternal)
    }

    @Test
    fun `unknown kind is kept and not treated as internal or external`() {
        val msg = known("""{"t":"displays","id":"1","items":[{"index":0,"id":"x","kind":"projector"}]}""") as CompanionMessage.Displays
        val d = msg.items.single()
        assertEquals("projector", d.kind)
        assertFalse(d.isInternal || d.isExternal)
    }

    @Test
    fun `empty list does not claim id support`() {
        assertFalse(DisplayList().supportsIds)
        assertFalse(DisplayList(listOf(Display(index = 0, id = "a"), Display(index = 1))).supportsIds)
    }

    @Test
    fun `moveToDisplay sends displayId, or the index for old companions`() {
        val (name, args) = sysArgs(SystemCommand.MoveToDisplay("""\\?\DISPLAY#GSM5B7F#5&1"""))
        assertEquals("moveToDisplay", name)
        assertEquals("""\\?\DISPLAY#GSM5B7F#5&1""", args["displayId"]!!.jsonPrimitive.content)
        assertNull(args["display"])

        val (_, legacy) = sysArgs(SystemCommand.MoveToDisplay(displayId = null, legacyIndex = 1))
        assertEquals("1", legacy["display"]!!.jsonPrimitive.content)
        assertNull(legacy["displayId"])
    }

    @Test(expected = IllegalArgumentException::class)
    fun `moveToDisplay needs a target`() {
        SystemCommand.MoveToDisplay(displayId = null, legacyIndex = null)
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
    fun `state system carries displayId and preferredDisplayId, old state still decodes`() {
        val state = (
            known(
                """{"t":"state","vlc":{"running":true,"level":2},"system":{"display":1,"displayId":"tv-id","preferredDisplayId":"tv-id","keepAwake":true}}""",
            ) as CompanionMessage.State
            ).toPlayerState()
        assertEquals(1, state.system!!.display)
        assertEquals("tv-id", state.system!!.displayId)
        assertEquals("tv-id", state.system!!.preferredDisplayId)

        val old = (
            known("""{"t":"state","vlc":{"running":true,"level":2},"system":{"display":0}}""") as CompanionMessage.State
            ).toPlayerState()
        assertEquals(0, old.system!!.display)
        assertNull(old.system!!.displayId)
        assertNull(old.system!!.preferredDisplayId)
    }
}
