package app.sofabird.data.vlc

import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.TrackKind
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.client.engine.mock.respondError
import io.ktor.client.plugins.websocket.WebSockets
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ConnectException

/**
 * Polling and command flow against a scripted VLC (Ktor MockEngine). Time is virtual: the
 * backend's own scope is the test scheduler, so `advanceTimeBy` drives the poll loop.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class VlcHttpBackendTest {

    private val statusPlaying = fixture("status-playing.json")
    private val statusStopped = fixture("status-stopped.json")
    private val tracksPage = fixture("tracks-custom-page.json")
    private val statusPaused = statusPlaying.replace("\"state\":\"playing\"", "\"state\":\"paused\"")

    /**
     * Records every request; `script` decides the answer by path and query. The engine runs on the
     * test dispatcher so the whole flow stays on virtual time.
     */
    private class FakeVlc(
        testDispatcher: CoroutineDispatcher,
        val script: (path: String, query: String?, auth: String?) -> Pair<Int, String>,
    ) {
        val requests = mutableListOf<String>()
        val engine = MockEngine.create {
            dispatcher = testDispatcher
            addHandler { request ->
                val path = request.url.encodedPath
                val query = request.url.encodedQuery.ifEmpty { null }
                requests += path + (query?.let { "?$it" } ?: "")
                val (code, body) = script(path, query, request.headers["Authorization"])
                if (code == 200) {
                    respond(body, HttpStatusCode.OK, headersOf("Content-Type", "text/plain"))
                } else {
                    respondError(HttpStatusCode.fromValue(code), body)
                }
            }
        }
        val client = HttpClient(engine) { install(WebSockets); expectSuccess = false }
    }

    private fun TestScope.fake(script: (path: String, query: String?, auth: String?) -> Pair<Int, String>) =
        FakeVlc(StandardTestDispatcher(testScheduler), script)

    private fun TestScope.backend(fake: FakeVlc, password: String = "secret", now: () -> Long = { 1_000L }) =
        VlcHttpBackend(
            api = VlcHttpApi(fake.client, "pc", 8080, password),
            hostName = "HTPC",
            scope = this,
            clock = now,
        )

    @Test
    fun `level 1 detected, status polled every second while playing, tracks page merged`() = runTest(StandardTestDispatcher()) {
        val fake = fake { path, _, auth ->
            assertEquals("Basic " + java.util.Base64.getEncoder().encodeToString(":secret".toByteArray()), auth)
            when (path) {
                "/requests/tracks.json" -> 200 to tracksPage
                "/requests/status.json" -> 200 to statusPlaying
                else -> 404 to ""
            }
        }
        val backend = backend(fake)
        backend.connect()
        runCurrent()

        val connected = backend.connection.value as ConnectionState.Connected
        assertEquals(1, connected.level)
        assertEquals("HTPC", connected.hostName)
        assertTrue(backend.capabilities.value.currentTrack)
        val state = backend.state.value
        assertEquals(PlaybackStatus.PLAYING, state.playback.state)
        assertEquals(4, state.tracks.currentAudio)
        assertEquals(10, state.tracks.currentSubtitle)
        assertEquals(40, state.chapters.names!!.size)
        assertEquals("http://pc:8080/art?item=3", state.item.artUrl)
        assertEquals(1_000L, state.playback.updatedAt)

        val statusPolls = fake.requests.count { it.startsWith("/requests/status.json") }
        advanceTimeBy(3_050)
        runCurrent()
        val after = fake.requests.count { it.startsWith("/requests/status.json") }
        assertEquals(3, after - statusPolls) // 1 s interval while playing

        backend.disconnect()
        assertEquals(ConnectionState.Disconnected, backend.connection.value)
    }

    @Test
    fun `level 0 when tracks page is 404, polls every 3 s when stopped, track remembered and reset on plid change`() = runTest(StandardTestDispatcher()) {
        var status = statusStopped
        val fake = fake { path, _, _ ->
            when (path) {
                "/requests/tracks.json" -> 404 to "<title>404</title>"
                "/requests/status.json" -> 200 to status
                else -> 404 to ""
            }
        }
        val backend = backend(fake)
        backend.connect()
        runCurrent()
        assertEquals(0, (backend.connection.value as ConnectionState.Connected).level)
        assertFalse(backend.capabilities.value.currentTrack)
        assertEquals(PlaybackStatus.STOPPED, backend.state.value.playback.state)

        val before = fake.requests.count { it.startsWith("/requests/status.json") }
        advanceTimeBy(3_100)
        runCurrent()
        assertEquals(1, fake.requests.count { it.startsWith("/requests/status.json") } - before) // 3 s interval

        // Start playing; choose a track on level 0 — remembered client-side.
        status = statusPlaying
        advanceTimeBy(3_100)
        runCurrent()
        assertNull(backend.state.value.tracks.currentAudio)
        val result = backend.send(PlayerCommand.SelectTrack(TrackKind.AUDIO, 2))
        assertTrue(result.isSuccess)
        assertTrue(fake.requests.any { it == "/requests/status.json?command=audio_track&val=2" })
        assertEquals(2, backend.state.value.tracks.currentAudio)

        // Another item (currentplid changes) → the remembered track is dropped.
        status = statusPlaying.replace("\"currentplid\":3", "\"currentplid\":5")
        advanceTimeBy(1_100)
        runCurrent()
        assertEquals(5, backend.state.value.item.plId)
        assertNull(backend.state.value.tracks.currentAudio)
        backend.disconnect()
    }

    @Test
    fun `command applies returned status immediately and fetches a clean status after 500 ms`() = runTest(StandardTestDispatcher()) {
        var commandSeen = false
        val fake = fake { path, query, _ ->
            when {
                path == "/requests/tracks.json" -> 404 to ""
                query == "command=pl_forcepause" -> { commandSeen = true; 200 to statusPaused }
                path == "/requests/status.json" -> 200 to (if (commandSeen) statusPaused else statusPlaying)
                else -> 404 to ""
            }
        }
        val backend = backend(fake)
        backend.connect()
        runCurrent()
        assertEquals(PlaybackStatus.PLAYING, backend.state.value.playback.state)

        val polls = fake.requests.size
        assertTrue(backend.send(PlayerCommand.Pause).isSuccess)
        assertEquals(PlaybackStatus.PAUSED, backend.state.value.playback.state) // applied from the command response
        assertEquals("/requests/status.json?command=pl_forcepause", fake.requests[polls])

        advanceTimeBy(600)
        runCurrent()
        assertTrue(fake.requests.drop(polls + 1).contains("/requests/status.json")) // clean status
        backend.disconnect()
    }

    @Test
    fun `relative seek encodes plus sign and mute is emulated with volume 0`() = runTest(StandardTestDispatcher()) {
        val fake = fake { path, _, _ ->
            if (path == "/requests/tracks.json") 404 to "" else 200 to statusStopped.replace("\"volume\":266", "\"volume\":200")
        }
        val backend = backend(fake)
        backend.connect()
        runCurrent()
        backend.send(PlayerCommand.SeekBy(10))
        assertTrue(fake.requests.contains("/requests/status.json?command=seek&val=%2B10"))
        backend.send(PlayerCommand.Mute(true))
        assertTrue(fake.requests.contains("/requests/status.json?command=volume&val=0"))
        assertTrue(backend.state.value.volume.muted)
        backend.send(PlayerCommand.Mute(false))
        assertTrue(fake.requests.contains("/requests/status.json?command=volume&val=200"))
        assertFalse(backend.state.value.volume.muted)
        backend.disconnect()
    }

    @Test
    fun `401 is a terminal WrongPassword failure, 403 is NoPasswordSet`() = runTest(StandardTestDispatcher()) {
        val fake401 = fake { _, _, _ -> 401 to "" }
        val b1 = backend(fake401)
        b1.connect()
        runCurrent()
        assertEquals(ConnectionState.Failed(ConnectionError.WrongPassword), b1.connection.value)
        val count = fake401.requests.size
        advanceTimeBy(10_000)
        runCurrent()
        assertEquals(count, fake401.requests.size) // no retries until the user changes the password
        b1.disconnect()

        val fake403 = fake { _, _, _ -> 403 to "" }
        val b2 = backend(fake403)
        b2.connect()
        runCurrent()
        assertEquals(ConnectionState.Failed(ConnectionError.NoPasswordSet), b2.connection.value)
        b2.disconnect()
    }

    @Test
    fun `lua error page and empty body are classified and refused connection reconnects with backoff`() = runTest(StandardTestDispatcher()) {
        val luaFake = fake { path, _, _ ->
            if (path == "/requests/tracks.json") 404 to "" else 200 to "<html><head><title>Error loading /requests/status.json</title></head></html>"
        }
        val b1 = backend(luaFake)
        b1.connect()
        runCurrent()
        val c1 = b1.connection.value as ConnectionState.Connecting
        assertTrue(c1.lastError is ConnectionError.LuaError)
        b1.disconnect()

        val emptyFake = fake { path, _, _ -> if (path == "/requests/tracks.json") 404 to "" else 200 to "" }
        val b2 = backend(emptyFake)
        b2.connect()
        runCurrent()
        assertEquals(ConnectionError.EmptyBody, (b2.connection.value as ConnectionState.Connecting).lastError)
        b2.disconnect()

        val refused = fake { _, _, _ -> throw ConnectException("Connection refused") }
        val b3 = backend(refused)
        b3.connect()
        runCurrent()
        assertEquals(ConnectionError.Refused, (b3.connection.value as ConnectionState.Connecting).lastError)
        val n = refused.requests.size
        advanceTimeBy(1_600) // backoff 1 s (+jitter) → one more attempt
        runCurrent()
        assertTrue(refused.requests.size > n)
        // A command while offline is buffered and then fails after the 5 s window.
        val result = b3.send(PlayerCommand.Stop)
        assertTrue(result.isFailure)
        b3.disconnect()
    }

    @Test
    fun `after three failures connection becomes Connecting and recovers`() = runTest(StandardTestDispatcher()) {
        var fail = false
        val fake = fake { path, _, _ ->
            if (path == "/requests/tracks.json") 404 to ""
            else if (fail) throw java.net.SocketTimeoutException("timeout")
            else 200 to statusPlaying
        }
        val backend = backend(fake)
        backend.connect()
        runCurrent()
        assertTrue(backend.connection.value is ConnectionState.Connected)

        fail = true
        advanceTimeBy(1_100); runCurrent()
        assertTrue(backend.connection.value is ConnectionState.Degraded)
        advanceTimeBy(1_600); runCurrent()
        advanceTimeBy(2_600); runCurrent()
        assertTrue(backend.connection.value is ConnectionState.Connecting)
        assertEquals(ConnectionError.Timeout, (backend.connection.value as ConnectionState.Connecting).lastError)

        fail = false
        advanceTimeBy(20_000); runCurrent()
        assertTrue(backend.connection.first { it is ConnectionState.Connected } is ConnectionState.Connected)
        backend.disconnect()
    }

    private fun fixture(name: String): String =
        checkNotNull(VlcHttpBackendTest::class.java.classLoader!!.getResourceAsStream(name)) { "fixture $name" }
            .bufferedReader().use { it.readText() }
}
