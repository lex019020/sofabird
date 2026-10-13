package dev.vlcremote.app.data.player

import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.VlcInfo
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Connect-or-pair state machine against a scripted companion. */
@OptIn(ExperimentalCoroutinesApi::class)
class PairingFlowTest {

    private val target = PairingTarget(host = "192.168.0.10", name = "HTPC", pcId = "guid-1")

    private fun ok(token: String?) = TestResult.CompanionOk(
        name = "HTPC",
        pcId = "guid-1",
        token = token,
        capabilities = Capabilities.NONE,
        vlc = VlcInfo(running = true, level = 2),
    )

    /** Fake companion + storage: `answer` decides the reply to each `hello`. */
    private class FakeLink(
        var token: String? = null,
        val answer: (token: String?, code: String?) -> TestResult,
    ) : CompanionLink {
        val hellos = mutableListOf<Pair<String?, String?>>()
        val connected = mutableListOf<Pair<PairingTarget, String?>>()
        var gate: CompletableDeferred<Unit>? = null

        override suspend fun storedToken(target: PairingTarget): String? = token

        override suspend fun hello(target: PairingTarget, token: String?, pairCode: String?): TestResult {
            hellos += token to pairCode
            gate?.await()
            return answer(token, pairCode)
        }

        override suspend fun forgetToken(target: PairingTarget) {
            token = null
        }

        override suspend fun connect(target: PairingTarget, welcome: TestResult.CompanionOk, newToken: String?): SavedServer {
            if (newToken != null) token = newToken
            connected += target to newToken
            return SavedServer(id = welcome.pcId ?: "id", name = welcome.name, host = target.host, mode = ServerMode.COMPANION, pcId = welcome.pcId)
        }
    }

    private fun TestScope.flow(link: CompanionLink) = PairingFlow(this, link)

    @Test
    fun `paired phone connects straight away`() = runTest {
        val link = FakeLink(token = "tok") { token, _ -> if (token == "tok") ok(null) else TestResult.Failed(ConnectionError.PairingRequired) }
        val flow = flow(link)

        flow.start(target)
        assertTrue(flow.state.value is PairingState.Connecting)
        runCurrent()

        val done = flow.state.value as PairingState.Done
        assertEquals(false, done.newlyPaired)
        assertEquals("guid-1", done.server.id)
        assertEquals(listOf<Pair<String?, String?>>("tok" to null), link.hellos)
        assertEquals(listOf(target to null), link.connected)

        flow.consumeDone()
        assertEquals(PairingState.Idle, flow.state.value)
    }

    @Test
    fun `pairing required asks for the code and pairs with it`() = runTest {
        val link = FakeLink { token, code ->
            when {
                code == "123456" -> ok("new-token")
                token == null -> TestResult.Failed(ConnectionError.PairingRequired)
                else -> ok(null)
            }
        }
        val flow = flow(link)

        flow.start(target, auto = true)
        assertEquals(PairingState.Connecting(target, auto = true), flow.state.value)
        runCurrent()
        assertEquals(PairingState.EnterCode(target), flow.state.value)

        // Incomplete codes are ignored.
        flow.submitCode("123")
        assertEquals(1, link.hellos.size)

        flow.submitCode("123456")
        assertTrue((flow.state.value as PairingState.EnterCode).submitting)
        runCurrent()

        val done = flow.state.value as PairingState.Done
        assertTrue(done.newlyPaired)
        assertEquals("new-token", link.token)
        assertEquals(listOf<Pair<String?, String?>>(null to null, null to "123456"), link.hellos)
        assertEquals(listOf(target to "new-token"), link.connected)
    }

    @Test
    fun `wrong code keeps the dialog open with an error, the next code works`() = runTest {
        val link = FakeLink { _, code ->
            when (code) {
                null -> TestResult.Failed(ConnectionError.PairingRequired)
                "654321" -> ok("t")
                else -> TestResult.Failed(ConnectionError.InvalidPairCode)
            }
        }
        val flow = flow(link)
        flow.start(target)
        runCurrent()

        flow.submitCode("111111")
        runCurrent()
        val rejected = flow.state.value as PairingState.EnterCode
        assertEquals(ConnectionError.InvalidPairCode, rejected.error)
        assertEquals(1, rejected.attempt)
        assertEquals(false, rejected.submitting)
        assertTrue(link.connected.isEmpty())

        flow.submitCode("654321")
        runCurrent()
        assertTrue(flow.state.value is PairingState.Done)
    }

    @Test
    fun `expired code answered with pairing_required reads as invalid code`() = runTest {
        val link = FakeLink { _, _ -> TestResult.Failed(ConnectionError.PairingRequired) }
        val flow = flow(link)
        flow.start(target)
        runCurrent()
        flow.submitCode("000000")
        runCurrent()
        assertEquals(ConnectionError.InvalidPairCode, (flow.state.value as PairingState.EnterCode).error)
    }

    @Test
    fun `revoked token is dropped and pairing starts`() = runTest {
        val link = FakeLink(token = "stale") { _, code ->
            if (code == null) TestResult.Failed(ConnectionError.PairingRequired) else ok("fresh")
        }
        val flow = flow(link)
        flow.start(target)
        runCurrent()
        assertTrue(flow.state.value is PairingState.EnterCode)
        assertNull(link.token)
        flow.submitCode("123456")
        runCurrent()
        assertEquals("fresh", link.token)
    }

    @Test
    fun `network failure is shown and can be retried`() = runTest {
        var reachable = false
        val link = FakeLink(token = "tok") { _, _ -> if (reachable) ok(null) else TestResult.Failed(ConnectionError.Timeout) }
        val flow = flow(link)
        flow.start(target)
        runCurrent()
        assertEquals(PairingState.Failed(target, ConnectionError.Timeout), flow.state.value)
        assertEquals("tok", link.token) // a timeout says nothing about the token

        reachable = true
        flow.retry()
        runCurrent()
        assertTrue(flow.state.value is PairingState.Done)
    }

    @Test
    fun `network error while checking the code keeps the dialog`() = runTest {
        val link = FakeLink { _, code -> if (code == null) TestResult.Failed(ConnectionError.PairingRequired) else TestResult.Failed(ConnectionError.Timeout) }
        val flow = flow(link)
        flow.start(target)
        runCurrent()
        flow.submitCode("123456")
        runCurrent()
        val state = flow.state.value as PairingState.EnterCode
        assertEquals(ConnectionError.Timeout, state.error)
    }

    @Test
    fun `accepted code without a token is not treated as paired`() = runTest {
        val link = FakeLink { _, code -> if (code == null) TestResult.Failed(ConnectionError.PairingRequired) else ok(null) }
        val flow = flow(link)
        flow.start(target)
        runCurrent()
        flow.submitCode("123456")
        runCurrent()
        assertTrue(flow.state.value is PairingState.EnterCode)
        assertTrue(link.connected.isEmpty())
    }

    @Test
    fun `cancel stops a pending hello`() = runTest {
        val link = FakeLink { _, _ -> ok(null) }
        link.gate = CompletableDeferred()
        val flow = flow(link)
        flow.start(target)
        runCurrent()
        flow.cancel()
        link.gate!!.complete(Unit)
        runCurrent()
        assertEquals(PairingState.Idle, flow.state.value)
        assertTrue(link.connected.isEmpty())
    }
}
