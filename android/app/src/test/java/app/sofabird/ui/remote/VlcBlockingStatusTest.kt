package app.sofabird.ui.remote

import app.sofabird.domain.model.SavedServer
import app.sofabird.domain.model.findCompanion
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.model.VlcProcessStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class VlcBlockingStatusTest {

    private val online = ConnectionState.Connected("HTPC")

    @Test
    fun `companion states other than running replace the remote`() {
        for (status in listOf(
            VlcProcessStatus.NOT_RUNNING,
            VlcProcessStatus.STARTING,
            VlcProcessStatus.NO_CONTROL,
            VlcProcessStatus.NOT_INSTALLED,
        )) {
            assertEquals(status, vlcBlockingStatus(online, VlcInfo(running = false, level = 2, status = status)))
        }
        assertNull(vlcBlockingStatus(online, VlcInfo(running = true, level = 2, status = VlcProcessStatus.RUNNING)))
    }

    @Test
    fun `old companion without status falls back to running flag`() {
        assertEquals(VlcProcessStatus.NOT_RUNNING, vlcBlockingStatus(online, VlcInfo(running = false, level = 2)))
        assertNull(vlcBlockingStatus(online, VlcInfo(running = true, level = 2)))
        // Unknown future value: derived from `running` too.
        assertEquals(
            VlcProcessStatus.NOT_RUNNING,
            vlcBlockingStatus(online, VlcInfo(running = false, level = 2, status = VlcProcessStatus.UNKNOWN)),
        )
    }

    @Test
    fun `offline never blocks, a degraded link still does`() {
        assertNull(vlcBlockingStatus(ConnectionState.Disconnected, VlcInfo(running = false)))
        assertNull(vlcBlockingStatus(ConnectionState.Failed(ConnectionError.Timeout), VlcInfo(status = VlcProcessStatus.NO_CONTROL)))
        assertEquals(
            VlcProcessStatus.NO_CONTROL,
            vlcBlockingStatus(ConnectionState.Degraded("HTPC", 1), VlcInfo(status = VlcProcessStatus.NO_CONTROL)),
        )
    }

    @Test
    fun `saved companion matching prefers id, then guid, then address`() {
        val a = SavedServer(id = "a", name = "HTPC", host = "192.168.0.10", pcId = "guid-a")
        val b = SavedServer(id = "b", name = "Office", host = "192.168.0.11")
        val list = listOf(a, b)

        assertEquals(b, list.findCompanion("10.0.0.1", 47555, pcId = "guid-a", serverId = "b"))
        // DHCP moved the PC: found by its GUID.
        assertEquals(a, list.findCompanion("192.168.0.99", 47555, pcId = "guid-a"))
        assertEquals(b, list.findCompanion("192.168.0.11", 47555, pcId = null))
        // Same address, but another installation: not the same PC.
        assertNull(list.findCompanion("192.168.0.10", 47555, pcId = "guid-other"))
        // An unknown address: no match.
        assertNull(list.findCompanion("192.168.0.12", 47555, pcId = null))
    }
}
