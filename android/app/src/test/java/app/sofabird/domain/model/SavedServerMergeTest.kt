package app.sofabird.domain.model

import app.sofabird.data.player.PairingTarget
import app.sofabird.data.player.TestResult
import app.sofabird.data.player.mergePairedServer
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.VlcInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

/** Saved companions: Bluetooth address, LAN address found later, one entry per companion GUID. */
class SavedServerMergeTest {

    private val bt = "AA:BB:CC:DD:EE:FF"

    private val lanPaired = SavedServer(
        id = "guid-1", name = "HTPC", host = "192.168.0.10", mode = ServerMode.COMPANION, pcId = "guid-1",
    )
    private val btPaired = SavedServer(
        id = "guid-2", name = "Laptop", host = "", mode = ServerMode.COMPANION, pcId = "guid-2", btAddress = bt,
    )
    private val direct = SavedServer(id = "d", name = "VLC", host = "192.168.0.20", mode = ServerMode.DIRECT)

    private fun ok(pcId: String?, bt: String?, name: String = "PC") = TestResult.CompanionOk(
        name = name, pcId = pcId, token = "t", capabilities = Capabilities.NONE, vlc = VlcInfo(), bt = bt,
    )

    @Test
    fun `beacon of a bluetooth-paired PC fills in its LAN address`() {
        val list = listOf(lanPaired, btPaired, direct)
        val sighting = CompanionSighting(pcId = "guid-2", host = "192.168.0.33", wsPort = 47555, btAddress = bt)
        val match = list.matchSighting(sighting)
        assertSame(btPaired, match)
        val merged = match!!.mergeSighting(sighting)
        assertEquals("192.168.0.33", merged.host)
        assertEquals(bt, merged.btAddress)
        assertEquals("guid-2", merged.id) // same entry, same token
        assertEquals("Laptop", merged.name)
    }

    @Test
    fun `wifi-paired PC learns its bluetooth address and keeps it`() {
        val withBt = lanPaired.mergeSighting(CompanionSighting(pcId = "guid-1", btAddress = "aa:bb:cc:dd:ee:01"))
        assertEquals("AA:BB:CC:DD:EE:01", withBt.btAddress)
        assertEquals("192.168.0.10", withBt.host)
        // A later beacon without `bt` (radio off on the PC) does not erase it; a welcome without host keeps the host.
        val again = withBt.mergeSighting(CompanionSighting(pcId = "guid-1", host = "192.168.0.11", wsPort = 47555, btAddress = null))
        assertEquals("AA:BB:CC:DD:EE:01", again.btAddress)
        assertEquals("192.168.0.11", again.host)
        assertEquals(withBt, withBt.mergeSighting(CompanionSighting(pcId = "guid-1")))
    }

    @Test
    fun `welcome macs are kept for waking the PC and never cleared`() {
        val macs = listOf("BC:FC:E7:08:6A:02")
        val withMacs = lanPaired.mergeSighting(CompanionSighting(pcId = "guid-1", macs = macs))
        assertEquals(macs, withMacs.wakeMacs)
        assertEquals(macs, withMacs.mergeSighting(CompanionSighting(pcId = "guid-1", macs = emptyList())).wakeMacs)
        assertEquals(macs, withMacs.mergeSighting(CompanionSighting(pcId = "guid-1")).wakeMacs)
    }

    @Test
    fun `sightings match only by GUID or bluetooth address`() {
        val list = listOf(lanPaired, btPaired, direct)
        // Same IP, unknown GUID: could be another PC after DHCP — no match.
        assertNull(list.matchSighting(CompanionSighting(pcId = null, host = "192.168.0.10", wsPort = 47555)))
        assertNull(list.matchSighting(CompanionSighting(pcId = "guid-other", host = "192.168.0.10", wsPort = 47555)))
        // Bluetooth address without GUID.
        assertSame(btPaired, list.matchSighting(CompanionSighting(pcId = null, host = "10.0.0.5", btAddress = "aa-bb-cc-dd-ee-ff")))
        // Direct VLC entries are never touched.
        assertNull(listOf(direct).matchSighting(CompanionSighting(pcId = "guid-1", host = "192.168.0.20")))
    }

    @Test
    fun `findCompanion uses the bluetooth address and never matches a blank host`() {
        val list = listOf(lanPaired, btPaired)
        assertSame(btPaired, list.findCompanion(host = "", wsPort = 47555, pcId = null, btAddress = bt))
        assertNull(list.findCompanion(host = "", wsPort = 47555, pcId = null, btAddress = "11:22:33:44:55:66"))
        assertNull(listOf(btPaired.copy(btAddress = null)).findCompanion(host = "", wsPort = 47555, pcId = null))
        assertSame(lanPaired, list.findCompanion(host = "192.168.0.10", wsPort = 47555, pcId = null))
    }

    @Test
    fun `pairing over bluetooth creates an entry keyed by the companion GUID without a LAN address`() {
        val target = PairingTarget(host = "", name = "LAPTOP-TV", btAddress = bt)
        val server = mergePairedServer(existing = null, target = target, welcome = ok("guid-9", bt, name = "Laptop"), now = 5)
        assertEquals("guid-9", server.id)
        assertEquals("", server.host)
        assertEquals(bt, server.btAddress)
        assertEquals("Laptop", server.name)
        assertEquals(ServerMode.COMPANION, server.mode)
        assertEquals(5, server.lastUsedAt)
    }

    @Test
    fun `connecting over bluetooth to a wifi-paired PC keeps its LAN address`() {
        val target = PairingTarget(host = "", name = "HTPC", btAddress = bt)
        val server = mergePairedServer(existing = lanPaired, target = target, welcome = ok("guid-1", bt), now = 1)
        assertEquals("guid-1", server.id)
        assertEquals("192.168.0.10", server.host)
        assertEquals(bt, server.btAddress)
        assertEquals("HTPC", server.name)
    }

    @Test
    fun `connecting over the LAN updates the address and keeps the known bluetooth address`() {
        val target = PairingTarget(host = "192.168.0.44", wsPort = 47555, name = "Laptop")
        val server = mergePairedServer(existing = btPaired, target = target, welcome = ok("guid-2", bt = null), now = 1)
        assertEquals("192.168.0.44", server.host)
        assertEquals(bt, server.btAddress)
        assertEquals("guid-2", server.id)
    }

    @Test
    fun `display name falls back to the bluetooth address`() {
        assertEquals(bt, SavedServer(id = "x", name = "", host = "", btAddress = bt).displayName)
        assertEquals("192.168.0.1", SavedServer(id = "x", name = "", host = "192.168.0.1", btAddress = bt).displayName)
    }
}
