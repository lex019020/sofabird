package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.SleepTimerState
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SleepTimerProtocolTest {

    private fun args(command: SystemCommand): JsonObject {
        val sys = CompanionCodec.json.parseToJsonElement(CompanionCodec.encode(CommandMapping.toSys("1", command))) as JsonObject
        assertEquals("sleepTimer", sys["name"]!!.jsonPrimitive.content)
        return sys["args"] as JsonObject
    }

    private fun JsonObject.str(key: String) = this[key]?.jsonPrimitive?.content

    @Test
    fun `sleepTimer commands`() {
        val inMinutes = args(SystemCommand.SleepTimer(minutes = 30, sleepPc = true))
        assertEquals("30", inMinutes.str("minutes"))
        assertEquals("sleep", inMinutes.str("action"))
        assertNull(inMinutes.str("endOfItem"))

        val atTheEnd = args(SystemCommand.SleepTimer(endOfItem = true))
        assertEquals("true", atTheEnd.str("endOfItem"))
        assertEquals("pause", atTheEnd.str("action"))
        assertNull(atTheEnd.str("minutes"))

        val off = args(SystemCommand.SleepTimer())
        assertNull(off.str("minutes"))
        assertNull(off.str("endOfItem"))
    }

    @Test
    fun `state and capability`() {
        val state = CompanionCodec.decode(
            """{"t":"state","vlc":{"running":true},"system":{"sleepTimer":{"endsAt":1793311200000,"action":"sleep"}}}""",
        )
        val timer = ((state as DecodedMessage.Known).message as CompanionMessage.State).toPlayerState().system!!.sleepTimer!!
        assertEquals(1_793_311_200_000L, timer.endsAt)
        assertTrue(timer.sleepsPc)
        assertFalse(timer.endOfItem)
        assertTrue(Capabilities.fromWire(listOf("sleepTimer")).sleepTimer)
    }

    @Test
    fun `minutes left round up and stop at zero`() {
        val timer = SleepTimerState(endsAt = 10 * 60_000L)
        assertEquals(10L, timer.minutesLeft(0))
        assertEquals(10L, timer.minutesLeft(1))
        assertEquals(1L, timer.minutesLeft(10 * 60_000L - 1))
        assertEquals(0L, timer.minutesLeft(11 * 60_000L))
        assertNull(SleepTimerState(endOfItem = true).minutesLeft(0))
    }
}
