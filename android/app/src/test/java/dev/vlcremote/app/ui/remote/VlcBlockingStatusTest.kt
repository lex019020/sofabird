package dev.vlcremote.app.ui.remote

import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.domain.model.findCompanion
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.model.VlcProcessStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class VlcBlockingStatusTest {

    private val online = ConnectionState.Connected("HTPC", level = 2)

    @Test
    fun `companion states other than running replace the remote`() {
        for (status in listOf(
            VlcProcessStatus.NOT_RUNNING,
            VlcProcessStatus.STARTING,
            VlcProcessStatus.NO_CONTROL,
            VlcProcessStatus.NOT_INSTALLED,
        )) {
            assertEquals(status, vlcBlockingStatus(true, online, VlcInfo(running = false, level = 2, status = status)))
        }
        assertNull(vlcBlockingStatus(true, online, VlcInfo(running = true, level = 2, status = VlcProcessStatus.RUNNING)))
    }

    @Test
    fun `old companion without status falls back to running flag`() {
        assertEquals(VlcProcessStatus.NOT_RUNNING, vlcBlockingStatus(true, online, VlcInfo(running = false, level = 2)))
        assertNull(vlcBlockingStatus(true, online, VlcInfo(running = true, level = 2)))
        // Unknown future value: derived from `running` too.
        assertEquals(
            VlcProcessStatus.NOT_RUNNING,
            vlcBlockingStatus(true, online, VlcInfo(running = false, level = 2, status = VlcProcessStatus.UNKNOWN)),
        )
    }

    @Test
    fun `direct mode and offline never block`() {
        assertNull(vlcBlockingStatus(false, ConnectionState.Connected("pc", 1), VlcInfo(running = false, level = 1)))
        assertNull(vlcBlockingStatus(true, ConnectionState.Disconnected, VlcInfo(running = false)))
        assertNull(vlcBlockingStatus(true, ConnectionState.Failed(ConnectionError.Timeout), VlcInfo(status = VlcProcessStatus.NO_CONTROL)))
        assertEquals(
            VlcProcessStatus.NO_CONTROL,
            vlcBlockingStatus(true, ConnectionState.Degraded("HTPC", 1), VlcInfo(status = VlcProcessStatus.NO_CONTROL)),
        )
    }

    @Test
    fun `saved companion matching prefers id, then guid, then address`() {
        val a = SavedServer(id = "a", name = "HTPC", host = "192.168.0.10", mode = ServerMode.COMPANION, pcId = "guid-a")
        val b = SavedServer(id = "b", name = "Office", host = "192.168.0.11", mode = ServerMode.COMPANION)
        val direct = SavedServer(id = "d", name = "VLC", host = "192.168.0.12", mode = ServerMode.DIRECT)
        val list = listOf(a, b, direct)

        assertEquals(b, list.findCompanion("10.0.0.1", 47555, pcId = "guid-a", serverId = "b"))
        // DHCP moved the PC: found by its GUID.
        assertEquals(a, list.findCompanion("192.168.0.99", 47555, pcId = "guid-a"))
        assertEquals(b, list.findCompanion("192.168.0.11", 47555, pcId = null))
        // Same address, but another installation: not the same PC.
        assertNull(list.findCompanion("192.168.0.10", 47555, pcId = "guid-other"))
        // Direct-VLC entries are never companions.
        assertNull(list.findCompanion("192.168.0.12", 47555, pcId = null))
    }
}
