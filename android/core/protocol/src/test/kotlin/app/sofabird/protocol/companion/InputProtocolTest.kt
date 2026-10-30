package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.InputBatch
import app.sofabird.protocol.backend.KeyMod
import app.sofabird.protocol.backend.MouseButton
import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.backend.PcKey
import app.sofabird.protocol.model.Capabilities
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class InputProtocolTest {

    private fun json(input: PcInput) = CompanionCodec.encode(CommandMapping.toInput(input))

    @Test
    fun `mouse messages`() {
        assertEquals("""{"t":"input","kind":"move","dx":12,"dy":-3}""", json(PcInput.Move(12, -3)))
        assertEquals("""{"t":"input","kind":"click","button":"left"}""", json(PcInput.Click()))
        assertEquals("""{"t":"input","kind":"click","button":"right"}""", json(PcInput.Click(MouseButton.RIGHT)))
        assertEquals("""{"t":"input","kind":"down","button":"left"}""", json(PcInput.Press()))
        assertEquals("""{"t":"input","kind":"up","button":"middle"}""", json(PcInput.Release(MouseButton.MIDDLE)))
        assertEquals("""{"t":"input","kind":"scroll","dx":-60,"dy":120}""", json(PcInput.Scroll(dy = 120, dx = -60)))
    }

    @Test
    fun `moves and scrolls are clamped to what the companion takes`() {
        assertEquals("""{"t":"input","kind":"move","dx":4000,"dy":-4000}""", json(PcInput.Move(9000, -12_000)))
        assertEquals("""{"t":"input","kind":"scroll","dx":0,"dy":4000}""", json(PcInput.Scroll(dy = 5000)))
    }

    @Test
    fun `keyboard messages`() {
        assertEquals("""{"t":"input","kind":"text","text":"Привет, мир"}""", json(PcInput.Text("Привет, мир")))
        assertEquals("""{"t":"input","kind":"key","key":"enter"}""", json(PcInput.Key(PcKey.ENTER)))
        assertEquals("""{"t":"input","kind":"key","key":"pagedown"}""", json(PcInput.Key(PcKey.PAGE_DOWN)))
        assertEquals("""{"t":"input","kind":"key","key":"volumemute"}""", json(PcInput.Key(PcKey.VOLUME_MUTE)))
        // Modifiers in a fixed order, whatever order they were given in.
        assertEquals(
            """{"t":"input","kind":"key","key":"f4","mods":["ctrl","alt"]}""",
            json(PcInput.Key(PcKey.F4, setOf(KeyMod.ALT, KeyMod.CTRL))),
        )
    }

    @Test
    fun `every key has the companion's name`() {
        val names = PcKey.entries.map { it.wire }.toSet()
        val companion = (
            "enter backspace tab escape space left up right down home end pageup pagedown delete win f4 f5 f11 " +
                "volumeup volumedown volumemute a c d l m q s v z"
            ).split(" ").toSet()
        assertEquals(companion, names)
    }

    @Test
    fun `input decodes back and has no id`() {
        val message = (CompanionCodec.decode("""{"t":"input","kind":"move","dx":1,"dy":2}""") as DecodedMessage.Known).message
        assertEquals(CompanionMessage.Input("move", dx = 1, dy = 2), message)
        assertEquals(null, message.id)
    }

    @Test
    fun `capability`() {
        assertTrue(Capabilities.fromWire(listOf("input")).input)
        assertFalse(Capabilities.fromWire(listOf("moonlight")).input)
    }

    @Test
    fun `a batch merges consecutive moves and scrolls only`() {
        val batch = InputBatch()
        listOf(
            PcInput.Move(1, 2), PcInput.Move(3, -1), PcInput.Press(), PcInput.Move(5, 5), PcInput.Move(1, 0),
            PcInput.Release(), PcInput.Scroll(30), PcInput.Scroll(40, 10), PcInput.Text("a"),
        ).forEach(batch::add)

        assertEquals(
            listOf(
                PcInput.Move(4, 1), PcInput.Press(), PcInput.Move(6, 5), PcInput.Release(), PcInput.Scroll(70, 10), PcInput.Text("a"),
            ),
            batch.drain(),
        )
        assertTrue(batch.isEmpty)
        assertEquals(emptyList<PcInput>(), batch.drain())
    }

    @Test
    fun `moves that cancel out are not sent`() {
        val batch = InputBatch()
        batch.add(PcInput.Move(3, 3))
        batch.add(PcInput.Move(-3, -3))
        batch.add(PcInput.Click())
        assertEquals(listOf<PcInput>(PcInput.Click()), batch.drain())
    }
}
