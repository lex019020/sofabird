package app.sofabird.protocol.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID

/** `bt` in welcome/beacon//info and the address helper. */
class BluetoothProtocolTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    @Test
    fun `welcome carries the PC bluetooth address`() {
        val welcome = known("""{"t":"welcome","v":1,"name":"HTPC","id":"g","caps":["bluetooth"],"bt":"AA:BB:CC:DD:EE:FF"}""")
            as CompanionMessage.Welcome
        assertEquals("AA:BB:CC:DD:EE:FF", welcome.bt)
    }

    @Test
    fun `bt is optional everywhere`() {
        val welcome = known("""{"t":"welcome","v":1,"name":"HTPC"}""") as CompanionMessage.Welcome
        assertNull(welcome.bt)
        val beacon = known("""{"t":"beacon","name":"HTPC","id":"g"}""") as CompanionMessage.Beacon
        assertNull(beacon.bt)
        assertNull(CompanionCodec.decodeInfo("""{"name":"HTPC"}""").bt)
        val nullBt = known("""{"t":"beacon","name":"HTPC","bt":null}""") as CompanionMessage.Beacon
        assertNull(nullBt.bt)
    }

    @Test
    fun `beacon and info decode bt`() {
        val beacon = known("""{"t":"beacon","name":"HTPC","id":"g","wsPort":47555,"protocol":1,"needsPairing":false,"bt":"00:1A:7D:DA:71:13"}""")
            as CompanionMessage.Beacon
        assertEquals("00:1A:7D:DA:71:13", beacon.bt)
        val info = CompanionCodec.decodeInfo("""{"name":"HTPC","protocol":1,"wsPort":47555,"needsPairing":false,"bt":"00:1A:7D:DA:71:13"}""")
        assertEquals("00:1A:7D:DA:71:13", info.bt)
    }

    @Test
    fun `address normalization`() {
        assertEquals("AA:BB:CC:DD:EE:FF", BtAddress.normalize("aa:bb:cc:dd:ee:ff"))
        assertEquals("AA:BB:CC:DD:EE:FF", BtAddress.normalize(" aa-bb-cc-dd-ee-ff "))
        assertNull(BtAddress.normalize(null))
        assertNull(BtAddress.normalize(""))
        assertNull(BtAddress.normalize("AA:BB:CC:DD:EE"))
        assertNull(BtAddress.normalize("GG:BB:CC:DD:EE:FF"))
        assertNull(BtAddress.normalize("00:00:00:00:00:00"))
        assertTrue(BtAddress.same("aa:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE:FF"))
        assertFalse(BtAddress.same(null, null))
        assertFalse(BtAddress.same("AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:F0"))
    }

    @Test
    fun `service uuid is a valid uuid`() {
        assertEquals(COMPANION_RFCOMM_UUID, UUID.fromString(COMPANION_RFCOMM_UUID).toString())
    }
}
