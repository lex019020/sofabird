package dev.vlcremote.app.data.player

import dev.vlcremote.app.data.companion.CompanionSocket
import dev.vlcremote.app.data.companion.CompanionSocket.closeQuietly
import dev.vlcremote.app.data.companion.CompanionSocket.handshake
import dev.vlcremote.app.data.vlc.VlcErrorClassifier
import dev.vlcremote.app.data.vlc.VlcHttpApi
import dev.vlcremote.app.data.vlc.VlcHttpException
import dev.vlcremote.protocol.companion.CompanionMessage
import dev.vlcremote.protocol.companion.DeviceInfo
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.vlc.VlcStatusParser
import io.ktor.client.HttpClient
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.cancellation.CancellationException

sealed class TestResult {
    data class DirectOk(val level: Int, val version: String?, val state: String) : TestResult()
    data class CompanionOk(
        val name: String,
        val pcId: String?,
        val token: String?,
        val capabilities: Capabilities,
        val vlc: VlcInfo,
    ) : TestResult()

    data class Failed(val error: ConnectionError) : TestResult()
}

/** "Test connection" for the onboarding screen: one request, a precise verdict. */
class ConnectionTester(private val ktor: HttpClient) {

    suspend fun testDirect(host: String, port: Int, password: String): TestResult {
        val api = VlcHttpApi(ktor, host, port, password)
        return try {
            val status = VlcStatusParser.parseStatus(api.status().body)
            val level = try {
                val page = VlcStatusParser.parseTracks(api.tracks().body)
                if (page.audio != null || page.chapter != null || page.video != null) 1 else 0
            } catch (e: CancellationException) {
                throw e
            } catch (_: Throwable) {
                0
            }
            TestResult.DirectOk(level, status.version, status.state)
        } catch (e: CancellationException) {
            throw e
        } catch (e: VlcHttpException) {
            TestResult.Failed(e.error)
        } catch (e: Throwable) {
            TestResult.Failed(VlcErrorClassifier.fromThrowable(e))
        }
    }

    suspend fun testCompanion(
        host: String,
        wsPort: Int,
        token: String?,
        pairCode: String?,
        device: DeviceInfo,
    ): TestResult {
        return try {
            val result = withTimeoutOrNull(12_000) {
                val ws = CompanionSocket.open(ktor, host, wsPort)
                try {
                    val welcome = ws.handshake(CompanionMessage.Hello(device = device, token = token, pairCode = pairCode))
                    TestResult.CompanionOk(
                        name = welcome.name,
                        pcId = welcome.pcId,
                        token = welcome.token,
                        capabilities = Capabilities.fromWire(welcome.caps),
                        vlc = welcome.vlc,
                    )
                } finally {
                    ws.closeQuietly()
                }
            }
            result ?: TestResult.Failed(ConnectionError.Timeout)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            TestResult.Failed(CompanionSocket.classify(e))
        }
    }
}
