package dev.vlcremote.protocol.companion

import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.model.VlcProcessStatus
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
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
    fun `old companion without status derives it from running`() {
        val running = stateWith("""{"running":true,"version":"3.0.23","apiVersion":3,"level":2}""")
        assertNull(running.status)
        assertEquals(VlcProcessStatus.RUNNING, running.effectiveStatus)

        val stopped = stateWith("""{"running":false,"level":2}""")
        assertNull(stopped.status)
        assertEquals(VlcProcessStatus.NOT_RUNNING, stopped.effectiveStatus)

        // welcome and /info carry the same object
        val welcome = known("""{"t":"welcome","v":1,"name":"HTPC","caps":[],"vlc":{"running":true,"level":2}}""") as CompanionMessage.Welcome
        assertEquals(VlcProcessStatus.RUNNING, welcome.vlc.effectiveStatus)
        val info = CompanionCodec.decodeInfo("""{"name":"HTPC","vlc":{"running":false,"status":"noControl"},"needsPairing":false}""")
        assertEquals(VlcProcessStatus.NO_CONTROL, info.vlc!!.effectiveStatus)
    }

    @Test
    fun `unknown status value falls back without breaking the message`() {
        val vlc = stateWith("""{"running":false,"status":"hibernating","level":2}""")
        assertEquals(VlcProcessStatus.UNKNOWN, vlc.status)
        assertEquals(VlcProcessStatus.NOT_RUNNING, vlc.effectiveStatus)
        val vlcRunning = stateWith("""{"running":true,"status":"somethingNew"}""")
        assertEquals(VlcProcessStatus.RUNNING, vlcRunning.effectiveStatus)
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
        // Without status (direct mode / old companion) the round-trip keeps it absent.
        val plain = CompanionMessage.State(vlc = VlcInfo(running = true, level = 2))
        assertEquals(plain, known(CompanionCodec.encode(plain)))
    }

    @Test
    fun `exited keeps old-companion semantics`() {
        assertEquals(VlcProcessStatus.NOT_RUNNING, VlcInfo(running = true, status = VlcProcessStatus.RUNNING).exited().status)
        val old = VlcInfo(running = true).exited()
        assertNull(old.status)
        assertFalse(old.running)
        assertEquals(VlcProcessStatus.NOT_RUNNING, old.effectiveStatus)
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

        val unauthorized = CompanionMessage.Err(code = "unauthorized", msg = "bad code")
        // With a code: wrong / expired code.
        assertEquals(ConnectionError.InvalidPairCode, unauthorized.toHandshakeError(sentPairCode = true))
        // Without a code: an old companion asking for pairing.
        assertEquals(ConnectionError.PairingRequired, unauthorized.toHandshakeError(sentPairCode = false))

        val other = CompanionMessage.Err(code = "internal", msg = "boom")
        val mapped = other.toHandshakeError(sentPairCode = false)
        assertTrue(mapped is ConnectionError.Other)
        assertEquals("boom", (mapped as ConnectionError.Other).message)
    }
}
