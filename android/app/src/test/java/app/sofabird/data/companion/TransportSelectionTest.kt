package app.sofabird.data.companion

import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.companion.CompanionCodec
import app.sofabird.protocol.companion.CompanionMessage
import app.sofabird.protocol.companion.DecodedMessage
import app.sofabird.protocol.companion.DeviceInfo
import app.sofabird.protocol.companion.LanRoute
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayingItem
import app.sofabird.protocol.model.TransportKind
import app.sofabird.protocol.model.VlcInfo
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ConnectException

/** LAN first, RFCOMM fallback, back to the LAN — [CompanionBackend] against scripted links. */
@OptIn(ExperimentalCoroutinesApi::class)
class TransportSelectionTest {

    private val bt = "AA:BB:CC:DD:EE:FF"
    private val lanEndpoint = CompanionEndpoint(host = "192.168.0.10", port = 47555, btAddress = bt, pcId = "guid-1")

    /** A companion on the other end of one link: answers hello/getState/ping like the real one. */
    private class FakeLink(
        override val kind: TransportKind,
        private val refuse: ConnectionError? = null,
        private val title: String,
    ) : CompanionTransport {
        private val incoming = Channel<String>(Channel.UNLIMITED)
        val sent = mutableListOf<CompanionMessage>()
        var closed = false
            private set

        override suspend fun send(text: String) {
            check(!closed) { "send on a closed link" }
            val msg = (CompanionCodec.decode(text) as DecodedMessage.Known).message
            sent += msg
            when (msg) {
                is CompanionMessage.Hello -> reply(
                    if (refuse == ConnectionError.PairingRequired) {
                        CompanionMessage.Err(code = "pairing_required")
                    } else {
                        CompanionMessage.Welcome(
                            name = "HTPC",
                            pcId = "guid-1",
                            caps = listOf("launchVlc", "bluetooth"),
                            vlc = VlcInfo(running = true),
                            bt = "AA:BB:CC:DD:EE:FF",
                        )
                    },
                )
                is CompanionMessage.GetState -> reply(
                    CompanionMessage.State(
                        vlc = VlcInfo(running = true, level = 2),
                        playback = Playback(state = PlaybackStatus.PLAYING, time = 904, length = 10705),
                        item = PlayingItem(title = title),
                    ),
                )
                is CompanionMessage.Ping -> reply(CompanionMessage.Pong())
                is CompanionMessage.Cmd -> reply(CompanionMessage.Ok(msg.id))
                else -> Unit
            }
        }

        private fun reply(msg: CompanionMessage) {
            incoming.trySend(CompanionCodec.encode(msg))
        }

        override suspend fun receive(): String? = if (closed) null else incoming.receiveCatching().getOrNull()

        override suspend fun close() {
            closed = true
            incoming.close()
        }

        /** A message the companion sends by itself (`event`, `state`). */
        fun push(msg: CompanionMessage) = reply(msg)

        /** The other side went away (Wi-Fi lost, PC asleep). */
        fun drop() {
            incoming.close()
        }

        fun hellos() = sent.filterIsInstance<CompanionMessage.Hello>()
    }

    private class FakeTransports : CompanionTransports {
        var lanUp = true
        var lanHangs = false
        var btUp = true
        var btStatus = BluetoothStatus.READY
        var refuse: ConnectionError? = null
        val ws = mutableListOf<FakeLink>()
        val rfcomm = mutableListOf<FakeLink>()
        var probes = 0
        var wsAttempts = 0

        override suspend fun openWebSocket(host: String, route: LanRoute): CompanionTransport {
            wsAttempts++
            if (lanHangs) awaitCancellation()
            if (!lanUp) throw ConnectException("Connection refused")
            return FakeLink(TransportKind.WIFI, refuse, title = "via wifi").also { ws += it }
        }

        override fun bluetoothStatus(address: String?): BluetoothStatus =
            if (address == null) BluetoothStatus.NO_ADDRESS else btStatus

        override suspend fun openRfcomm(address: String): CompanionTransport {
            if (!btUp) throw BluetoothConnectException("no answer")
            return FakeLink(TransportKind.BLUETOOTH, refuse, title = "via bluetooth").also { rfcomm += it }
        }

        override suspend fun probeLan(host: String, port: Int, pcId: String?, btAddress: String?): LanEndpoint? {
            probes++
            return if (lanUp && !lanHangs) LanEndpoint(host.ifBlank { "192.168.0.77" }, port, btAddress) else null
        }
    }

    private class Harness(val backend: CompanionBackend, val history: MutableList<ConnectionState>, val welcomes: MutableList<CompanionMessage.Welcome>)

    private fun TestScope.backend(
        transports: FakeTransports,
        endpoint: CompanionEndpoint = lanEndpoint,
        token: String? = "tok",
        bluetoothAllowed: Boolean = true,
    ): Harness {
        val welcomes = mutableListOf<CompanionMessage.Welcome>()
        val backend = CompanionBackend(
            transports = transports,
            endpoint = { endpoint },
            initialName = "HTPC",
            device = DeviceInfo("Galaxy S24", "phone-1"),
            credentials = CompanionCredentials(token = token),
            onToken = {},
            scope = backgroundScope,
            onWelcome = { welcome, _ -> welcomes += welcome },
            bluetoothAllowed = { bluetoothAllowed },
        )
        val history = mutableListOf<ConnectionState>()
        backgroundScope.launch(UnconfinedTestDispatcher(testScheduler)) { backend.connection.toList(history) }
        return Harness(backend, history, welcomes)
    }

    private val CompanionBackend.transport: TransportKind?
        get() = (connection.value as? ConnectionState.Connected)?.transport

    @Test
    fun `wifi is used when the LAN answers`() = runTest {
        val t = FakeTransports()
        val h = backend(t)
        h.backend.connect()
        runCurrent()

        assertEquals(TransportKind.WIFI, h.backend.transport)
        assertEquals(1, t.ws.size)
        assertTrue(t.rfcomm.isEmpty())
        assertEquals("tok", t.ws.single().hellos().single().token)
        assertEquals("AA:BB:CC:DD:EE:FF", h.welcomes.single().bt) // persisted by the controller
        assertEquals("via wifi", h.backend.state.value.item.title)
    }

    @Test
    fun `LAN refused falls back to RFCOMM with the same token`() = runTest {
        val t = FakeTransports().apply { lanUp = false }
        val h = backend(t)
        h.backend.connect()
        runCurrent()

        assertEquals(TransportKind.BLUETOOTH, h.backend.transport)
        assertEquals("tok", t.rfcomm.single().hellos().single().token)
        assertTrue(t.rfcomm.single().sent.any { it is CompanionMessage.GetState })
        assertEquals("via bluetooth", h.backend.state.value.item.title)
        assertNull(h.backend.bluetoothNeed.value)

        // Commands work over RFCOMM.
        assertTrue(h.backend.send(app.sofabird.protocol.backend.PlayerCommand.TogglePause).isSuccess)
    }

    @Test
    fun `silent LAN falls back after about three seconds`() = runTest {
        val t = FakeTransports().apply { lanHangs = true }
        val h = backend(t)
        h.backend.connect()
        advanceTimeBy(2_900)
        runCurrent()
        assertTrue(h.backend.connection.value is ConnectionState.Connecting)
        assertTrue(t.rfcomm.isEmpty())

        advanceTimeBy(200)
        runCurrent()
        assertEquals(TransportKind.BLUETOOTH, h.backend.transport)
    }

    @Test
    fun `back on the LAN the session moves to wifi without dropping`() = runTest {
        val t = FakeTransports().apply { lanUp = false }
        val h = backend(t)
        h.backend.connect()
        runCurrent()
        assertEquals(TransportKind.BLUETOOTH, h.backend.transport)
        val btLink = t.rfcomm.single()

        // BT pings every 15 s.
        advanceTimeBy(15_100)
        runCurrent()
        assertTrue(btLink.sent.any { it is CompanionMessage.Ping })
        assertEquals(TransportKind.BLUETOOTH, h.backend.transport)

        t.lanUp = true
        advanceTimeBy(5_000) // the probe runs every 20 s
        runCurrent()

        assertEquals(TransportKind.WIFI, h.backend.transport)
        assertTrue("old RFCOMM link closed", btLink.closed)
        val wsLink = t.ws.single()
        assertEquals("re-hello with the token", "tok", wsLink.hellos().single().token)
        assertTrue("state re-requested", wsLink.sent.any { it is CompanionMessage.GetState })
        assertEquals("via wifi", h.backend.state.value.item.title)

        // Never went through Connecting/Disconnected after the first connect.
        val afterFirst = h.history.dropWhile { it !is ConnectionState.Connected }
        assertTrue(afterFirst.toString(), afterFirst.all { it is ConnectionState.Connected })
        assertEquals(
            listOf(TransportKind.BLUETOOTH, TransportKind.WIFI),
            afterFirst.map { (it as ConnectionState.Connected).transport }.distinct(),
        )

        // WS pings every 30 s, not 15.
        val pingsBefore = wsLink.sent.count { it is CompanionMessage.Ping }
        advanceTimeBy(16_000)
        runCurrent()
        assertEquals(pingsBefore, wsLink.sent.count { it is CompanionMessage.Ping })
        advanceTimeBy(15_000)
        runCurrent()
        assertTrue(wsLink.sent.count { it is CompanionMessage.Ping } > pingsBefore)
    }

    @Test
    fun `losing wifi mid-session reconnects over bluetooth`() = runTest {
        val t = FakeTransports()
        val h = backend(t)
        h.backend.connect()
        runCurrent()
        assertEquals(TransportKind.WIFI, h.backend.transport)

        t.lanUp = false
        t.ws.single().drop()
        runCurrent()
        assertTrue(h.backend.connection.value is ConnectionState.Connecting)
        advanceTimeBy(2_000) // backoff after a clean close: ~1 s
        runCurrent()
        assertEquals(TransportKind.BLUETOOTH, h.backend.transport)
    }

    @Test
    fun `a PC going to sleep ends the link at once and counts as asleep until it answers`() = runTest {
        val t = FakeTransports()
        val h = backend(t, bluetoothAllowed = false)
        h.backend.connect()
        runCurrent()
        assertEquals(TransportKind.WIFI, h.backend.transport)

        t.lanUp = false
        t.ws.single().push(CompanionMessage.Event(name = "sleeping"))
        runCurrent()
        assertEquals(ConnectionError.PcAsleep, (h.backend.connection.value as ConnectionState.Connecting).lastError)
        assertTrue("link closed", t.ws.single().closed)

        // Not in its last second: the first retry comes after ~8 s, and a failed one keeps it "asleep".
        advanceTimeBy(7_000)
        runCurrent()
        assertEquals(1, t.wsAttempts)
        advanceTimeBy(2_000)
        runCurrent()
        assertEquals(2, t.wsAttempts)
        assertEquals(ConnectionError.PcAsleep, (h.backend.connection.value as ConnectionState.Connecting).lastError)

        // Woken up ("Wake up the PC" knocks at once): connected again.
        t.lanUp = true
        h.backend.retryNow()
        runCurrent()
        assertEquals(TransportKind.WIFI, h.backend.transport)
    }

    @Test
    fun `no permission means no RFCOMM, and the user is asked`() = runTest {
        val t = FakeTransports().apply {
            lanUp = false
            btStatus = BluetoothStatus.NO_PERMISSION
        }
        val h = backend(t)
        h.backend.connect()
        runCurrent()

        assertTrue(t.rfcomm.isEmpty())
        assertEquals(BluetoothStatus.NO_PERMISSION, h.backend.bluetoothNeed.value)
        val c = h.backend.connection.value as ConnectionState.Connecting
        assertEquals(ConnectionError.Refused, c.lastError) // the LAN error is the explanation

        // Permission granted → retry right away, without waiting for the backoff.
        t.btStatus = BluetoothStatus.READY
        h.backend.retryNow()
        runCurrent()
        assertEquals(TransportKind.BLUETOOTH, h.backend.transport)
        assertNull(h.backend.bluetoothNeed.value)
    }

    @Test
    fun `not bonded or radio off - no RFCOMM attempt`() = runTest {
        val t = FakeTransports().apply {
            lanUp = false
            btStatus = BluetoothStatus.NOT_BONDED
        }
        val h = backend(t)
        h.backend.connect()
        runCurrent()
        assertTrue(t.rfcomm.isEmpty())
        assertNull("nothing the user can fix from the app", h.backend.bluetoothNeed.value)

        t.btStatus = BluetoothStatus.OFF
        advanceTimeBy(3_000)
        runCurrent()
        assertTrue(t.rfcomm.isEmpty())
        assertEquals(BluetoothStatus.OFF, h.backend.bluetoothNeed.value)
    }

    @Test
    fun `no known bluetooth address or setting off - LAN only`() = runTest {
        val t = FakeTransports().apply { lanUp = false }
        val h = backend(t, endpoint = lanEndpoint.copy(btAddress = null))
        h.backend.connect()
        runCurrent()
        assertTrue(t.rfcomm.isEmpty())
        assertTrue(h.backend.connection.value is ConnectionState.Connecting)

        val t2 = FakeTransports().apply { lanUp = false }
        val h2 = backend(t2, bluetoothAllowed = false)
        h2.backend.connect()
        runCurrent()
        assertTrue(t2.rfcomm.isEmpty())
    }

    @Test
    fun `pairing refusal on the LAN is final - no bluetooth retry`() = runTest {
        val t = FakeTransports().apply { refuse = ConnectionError.PairingRequired }
        val h = backend(t)
        h.backend.connect()
        runCurrent()
        assertEquals(ConnectionState.Failed(ConnectionError.PairingRequired), h.backend.connection.value)
        assertTrue(t.rfcomm.isEmpty())
    }

    @Test
    fun `PC known only by bluetooth connects over RFCOMM and finds its LAN address later`() = runTest {
        val t = FakeTransports()
        val found = mutableListOf<LanEndpoint>()
        val backend = CompanionBackend(
            transports = t,
            endpoint = { CompanionEndpoint(host = "", port = 47555, btAddress = bt, pcId = "guid-1") },
            initialName = "HTPC",
            device = DeviceInfo("Galaxy S24", "phone-1"),
            credentials = CompanionCredentials(token = "tok"),
            onToken = {},
            scope = backgroundScope,
            onLanFound = { found += it },
        )
        backend.connect()
        runCurrent()
        assertTrue("no host: WebSocket not even tried", t.ws.isEmpty())
        assertEquals(TransportKind.BLUETOOTH, (backend.connection.value as ConnectionState.Connected).transport)

        advanceTimeBy(20_100)
        runCurrent()
        assertEquals(listOf(LanEndpoint("192.168.0.77", 47555, bt)), found)
        assertEquals(TransportKind.WIFI, (backend.connection.value as ConnectionState.Connected).transport)
    }

    @Test
    fun `bluetooth failure of a bluetooth-only PC is reported as such`() = runTest {
        val t = FakeTransports().apply { btUp = false }
        val backend = CompanionBackend(
            transports = t,
            endpoint = { CompanionEndpoint(host = "", port = 47555, btAddress = bt) },
            initialName = "HTPC",
            device = DeviceInfo("Galaxy S24", "phone-1"),
            credentials = CompanionCredentials(token = "tok"),
            onToken = {},
            scope = backgroundScope,
        )
        backend.connect()
        runCurrent()
        val c = backend.connection.value as ConnectionState.Connecting
        assertEquals(ConnectionError.BluetoothFailed, c.lastError)
        assertFalse(c.attempt == 0)
    }

    @Test
    fun `touchpad input goes on the current link, in order and without a reply`() = runTest {
        val t = FakeTransports().apply { lanUp = false }
        val h = backend(t)
        // Before the link is up nothing is buffered: a stale move is no use later.
        h.backend.sendInput(PcInput.Move(1, 1))

        h.backend.connect()
        runCurrent()
        h.backend.sendInput(PcInput.Press())
        h.backend.sendInput(PcInput.Move(12, -3))
        h.backend.sendInput(PcInput.Release())

        assertEquals(
            listOf(
                CompanionMessage.Input("down", button = "left"),
                CompanionMessage.Input("move", dx = 12, dy = -3),
                CompanionMessage.Input("up", button = "left"),
            ),
            t.rfcomm.single().sent.filterIsInstance<CompanionMessage.Input>(),
        )
    }

    @Test
    fun `input on a link that broke is dropped quietly`() = runTest {
        val t = FakeTransports()
        val h = backend(t)
        h.backend.connect()
        runCurrent()
        t.ws.single().close() // sending on it now throws
        h.backend.sendInput(PcInput.Click()) // no exception
    }
}
