package app.sofabird.protocol.companion

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class MagicPacketTest {

    @Test
    fun `six FF bytes then the mac sixteen times`() {
        val packet = MagicPacket.of("BC:FC:E7:08:6A:02")!!
        assertEquals(102, packet.size)
        assertArrayEquals(ByteArray(6) { 0xFF.toByte() }, packet.copyOfRange(0, 6))
        val mac = byteArrayOf(0xBC.toByte(), 0xFC.toByte(), 0xE7.toByte(), 0x08, 0x6A, 0x02)
        for (i in 0 until 16) assertArrayEquals(mac, packet.copyOfRange(6 + i * 6, 12 + i * 6))
        assertArrayEquals(packet, MagicPacket.of("bc-fc-e7-08-6a-02"))
        assertArrayEquals(packet, MagicPacket.of("bcfce7086a02"))
    }

    @Test
    fun `not a mac`() {
        assertNull(MagicPacket.of("BC:FC:E7:08:6A"))
        assertNull(MagicPacket.of("ZZ:FC:E7:08:6A:02"))
        assertNull(MagicPacket.of(""))
    }

    @Test
    fun `welcome carries the macs`() {
        val welcome = CompanionCodec.decode(
            """{"t":"welcome","v":1,"name":"HTPC","id":"pc","caps":[],"macs":["BC:FC:E7:08:6A:02","04:68:74:9A:2B:FB"]}""",
        )
        val known = (welcome as DecodedMessage.Known).message as CompanionMessage.Welcome
        assertEquals(listOf("BC:FC:E7:08:6A:02", "04:68:74:9A:2B:FB"), known.macs)
    }
}
