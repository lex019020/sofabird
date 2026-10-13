package dev.vlcremote.app.data.player

import dev.vlcremote.app.data.companion.CompanionTransport
import dev.vlcremote.app.data.companion.CompanionTransports
import dev.vlcremote.app.data.companion.classifyCompanionError
import dev.vlcremote.app.data.companion.handshake
import dev.vlcremote.app.data.vlc.VlcErrorClassifier
import dev.vlcremote.app.data.vlc.VlcHttpApi
import dev.vlcremote.app.data.vlc.VlcHttpException
import dev.vlcremote.protocol.companion.BtAddress
import dev.vlcremote.protocol.companion.CompanionMessage
import dev.vlcremote.protocol.companion.DeviceInfo
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.vlc.VlcStatusParser
import io.ktor.client.HttpClient
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
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
        /** PC Bluetooth address from `welcome`, normalized. */
        val bt: String? = null,
    ) : TestResult()

    data class Failed(val error: ConnectionError) : TestResult()
}

/** "Test connection" for the onboarding screen: one request, a precise verdict. */
class ConnectionTester(private val ktor: HttpClient, private val transports: CompanionTransports) {

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

    /** One `hello` over the LAN WebSocket; the link is closed afterwards. */
    suspend fun testCompanion(
        host: String,
        wsPort: Int,
        token: String?,
        pairCode: String?,
        device: DeviceInfo,
    ): TestResult = helloOver(token, pairCode, device, timeoutMs = 12_000) { transports.openWebSocket(host, wsPort) }

    /** The same over RFCOMM to the bonded PC at [address] (first connection via Bluetooth). */
    suspend fun testCompanionBluetooth(
        address: String,
        token: String?,
        pairCode: String?,
        device: DeviceInfo,
    ): TestResult = helloOver(token, pairCode, device, timeoutMs = 25_000, timeoutError = ConnectionError.BluetoothFailed) {
        transports.openRfcomm(address)
    }

    private suspend fun helloOver(
        token: String?,
        pairCode: String?,
        device: DeviceInfo,
        timeoutMs: Long,
        timeoutError: ConnectionError = ConnectionError.Timeout,
        open: suspend () -> CompanionTransport,
    ): TestResult {
        return try {
            val result = withTimeoutOrNull(timeoutMs) {
                val link = open()
                try {
                    val welcome = link.handshake(CompanionMessage.Hello(device = device, token = token, pairCode = pairCode))
                    TestResult.CompanionOk(
                        name = welcome.name,
                        pcId = welcome.pcId,
                        token = welcome.token,
                        capabilities = Capabilities.fromWire(welcome.caps),
                        vlc = welcome.vlc,
                        bt = BtAddress.normalize(welcome.bt),
                    )
                } finally {
                    withContext(NonCancellable) { link.close() }
                }
            }
            result ?: TestResult.Failed(timeoutError)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            TestResult.Failed(classifyCompanionError(e))
        }
    }
}
