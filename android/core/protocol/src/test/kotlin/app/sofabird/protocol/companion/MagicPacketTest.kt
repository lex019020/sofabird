package app.sofabird.protocol.companion

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class MagicPacketTest {

    @Test
    fun `six FF bytes then the mac sixteen times`() {
        val packet = MagicPacket.of("02:1A:2B:3C:4D:5E")!!
        assertEquals(102, packet.size)
        assertArrayEquals(ByteArray(6) { 0xFF.toByte() }, packet.copyOfRange(0, 6))
        val mac = byteArrayOf(0x02, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E)
        for (i in 0 until 16) assertArrayEquals(mac, packet.copyOfRange(6 + i * 6, 12 + i * 6))
        assertArrayEquals(packet, MagicPacket.of("02-1a-2b-3c-4d-5e"))
        assertArrayEquals(packet, MagicPacket.of("021a2b3c4d5e"))
    }

    @Test
    fun `not a mac`() {
        assertNull(MagicPacket.of("02:1A:2B:3C:4D"))
        assertNull(MagicPacket.of("ZZ:1A:2B:3C:4D:5E"))
        assertNull(MagicPacket.of(""))
    }

    @Test
    fun `welcome carries the macs`() {
        val welcome = CompanionCodec.decode(
            """{"t":"welcome","v":1,"name":"HTPC","id":"pc","caps":[],"macs":["02:1A:2B:3C:4D:5E","02:1A:2B:3C:4D:5F"]}""",
        )
        val known = (welcome as DecodedMessage.Known).message as CompanionMessage.Welcome
        assertEquals(listOf("02:1A:2B:3C:4D:5E", "02:1A:2B:3C:4D:5F"), known.macs)
    }
}
