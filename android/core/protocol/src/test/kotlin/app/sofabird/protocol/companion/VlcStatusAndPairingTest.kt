package app.sofabird.protocol.companion

import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.model.VlcProcessStatus
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** `vlc.status` and the `pairing_required` handshake error (protocol/README.md). */
class VlcStatusAndPairingTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    private fun stateWith(vlc: String) = (known("""{"t":"state","vlc":$vlc}""") as CompanionMessage.State).toPlayerState().vlc

    @Test
    fun `every vlc status decodes`() {
        val expected = mapOf(
            "notInstalled" to VlcProcessStatus.NOT_INSTALLED,
            "notRunning" to VlcProcessStatus.NOT_RUNNING,
            "starting" to VlcProcessStatus.STARTING,
            "noControl" to VlcProcessStatus.NO_CONTROL,
            "running" to VlcProcessStatus.RUNNING,
        )
        for ((wire, status) in expected) {
            val vlc = stateWith("""{"running":${status == VlcProcessStatus.RUNNING},"status":"$wire","level":2}""")
            assertEquals(wire, status, vlc.status)
            assertEquals(wire, status, vlc.effectiveStatus)
            assertEquals(status, VlcProcessStatus.fromWire(wire))
        }
    }

    @Test
    fun `welcome and info carry the same vlc object`() {
        val welcome = known(
            """{"t":"welcome","v":1,"name":"HTPC","caps":[],"vlc":{"running":true,"status":"running","level":2}}""",
        ) as CompanionMessage.Welcome
        assertEquals(VlcProcessStatus.RUNNING, welcome.vlc.status)
        val info = CompanionCodec.decodeInfo("""{"name":"HTPC","vlc":{"running":false,"status":"noControl"},"needsPairing":false}""")
        assertEquals(VlcProcessStatus.NO_CONTROL, info.vlc!!.status)
    }

    @Test
    fun `unknown status value of a newer companion falls back to running`() {
        val vlc = stateWith("""{"running":false,"status":"hibernating","level":2}""")
        assertEquals(VlcProcessStatus.UNKNOWN, vlc.status)
        assertEquals(2, vlc.level)
        assertEquals(VlcProcessStatus.NOT_RUNNING, vlc.effectiveStatus)
        assertEquals(VlcProcessStatus.RUNNING, stateWith("""{"running":true,"status":"somethingNew"}""").effectiveStatus)
        assertEquals(VlcProcessStatus.UNKNOWN, VlcProcessStatus.fromWire(""))
        assertEquals(VlcProcessStatus.UNKNOWN, VlcProcessStatus.fromWire(null))
    }

    @Test
    fun `status round-trips through state`() {
        for (status in VlcProcessStatus.entries - VlcProcessStatus.UNKNOWN) {
            val state = CompanionMessage.State(vlc = VlcInfo(running = status == VlcProcessStatus.RUNNING, level = 2, status = status))
            val text = CompanionCodec.encode(state)
            val obj = CompanionCodec.json.parseToJsonElement(text) as JsonObject
            assertEquals(status.wire, (obj["vlc"] as JsonObject)["status"]!!.jsonPrimitive.content)
            assertEquals(state, known(text))
        }
    }

    @Test
    fun `exited means not running`() {
        val exited = VlcInfo(running = true, level = 2, status = VlcProcessStatus.RUNNING).exited()
        assertEquals(VlcProcessStatus.NOT_RUNNING, exited.status)
        assertFalse(exited.running)
        assertEquals(VlcProcessStatus.NOT_RUNNING, VlcInfo(status = VlcProcessStatus.NO_CONTROL).exited().status)
    }

    @Test
    fun `pairing_required error code decodes`() {
        val err = known("""{"t":"err","code":"pairing_required","msg":"pairing required"}""") as CompanionMessage.Err
        assertEquals(ErrorCode.PAIRING_REQUIRED, err.errorCode)
        assertEquals("pairing_required", ErrorCode.PAIRING_REQUIRED.wire)
        val reencoded = known(CompanionCodec.encode(err)) as CompanionMessage.Err
        assertEquals(err, reencoded)
        assertEquals(ErrorCode.UNKNOWN, ErrorCode.fromWire(""))
    }

    @Test
    fun `handshake errors map to pairing states`() {
        val pairingRequired = CompanionMessage.Err(code = "pairing_required", msg = "x")
        assertEquals(ConnectionError.PairingRequired, pairingRequired.toHandshakeError(sentPairCode = false))
        assertEquals(ConnectionError.PairingRequired, pairingRequired.toHandshakeError(sentPairCode = true))

        // With a code: wrong / expired code.
        val unauthorized = CompanionMessage.Err(code = "unauthorized", msg = "bad code")
        assertEquals(ConnectionError.InvalidPairCode, unauthorized.toHandshakeError(sentPairCode = true))
        // Without a code it is not about pairing (no valid hello in time): an ordinary failure.
        assertEquals(ConnectionError.Other("hello timeout"), CompanionMessage.Err(code = "unauthorized", msg = "hello timeout").toHandshakeError(sentPairCode = false))

        val other = CompanionMessage.Err(code = "internal", msg = "boom")
        val mapped = other.toHandshakeError(sentPairCode = false)
        assertTrue(mapped is ConnectionError.Other)
        assertEquals("boom", (mapped as ConnectionError.Other).message)
    }
}
