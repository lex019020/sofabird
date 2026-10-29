package app.sofabird.data.player

import app.sofabird.data.companion.CompanionHandshakeException
import app.sofabird.data.companion.CompanionTransport
import app.sofabird.data.companion.CompanionTransports
import app.sofabird.data.companion.classifyCompanionError
import app.sofabird.data.companion.handshake
import app.sofabird.data.companion.isTerminalHandshakeError
import app.sofabird.data.vlc.VlcErrorClassifier
import app.sofabird.data.vlc.VlcHttpApi
import app.sofabird.data.vlc.VlcHttpException
import app.sofabird.protocol.companion.BtAddress
import app.sofabird.protocol.companion.CompanionMessage
import app.sofabird.protocol.companion.DeviceInfo
import app.sofabird.protocol.companion.lanRoutes
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.vlc.VlcStatusParser
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
        /** The encrypted WebSocket used for this test: its port and the certificate to pin; `null` — plain. */
        val tlsPort: Int? = null,
        val certPin: String? = null,
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

    /**
     * One `hello` over the LAN WebSocket; the link is closed afterwards. Encrypted first ([lanRoutes]; a known
     * [certPin] is required), plain for an older companion; an encrypted success carries the certificate to pin.
     */
    suspend fun testCompanion(
        host: String,
        wsPort: Int,
        token: String?,
        pairCode: String?,
        device: DeviceInfo,
        tlsPort: Int? = null,
        certPin: String? = null,
    ): TestResult {
        var last: TestResult = TestResult.Failed(ConnectionError.Timeout)
        for (route in lanRoutes(wsPort, tlsPort, certPin)) {
            last = helloOver(token, pairCode, device, timeoutMs = if (route.tls) TLS_TRY_MS else 12_000) {
                transports.openWebSocket(host, route)
            }
            val result = last
            if (result is TestResult.CompanionOk) return if (route.tls) result.copy(tlsPort = route.port) else result.copy(certPin = null)
            // A refusal (wrong code, pairing needed, another certificate) is the companion's answer: do not go plain.
            if (result is TestResult.Failed && result.error.isTerminalHandshakeError) return result
        }
        return last
    }

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
                    val certificate = link.certificate
                    if (certificate != null && welcome.tls?.sha256?.equals(certificate, ignoreCase = true) == false) {
                        throw CompanionHandshakeException(ConnectionError.CertificateChanged, "welcome names another certificate")
                    }
                    TestResult.CompanionOk(
                        name = welcome.name,
                        pcId = welcome.pcId,
                        token = welcome.token,
                        capabilities = Capabilities.fromWire(welcome.caps),
                        vlc = welcome.vlc,
                        bt = BtAddress.normalize(welcome.bt),
                        // Plain: the companion says it has the encrypted one; the first session then pins it.
                        tlsPort = welcome.tls?.port,
                        certPin = certificate,
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

/** An encrypted try during pairing: a companion without it (older) must not hold up the plain one for long. */
private const val TLS_TRY_MS = 5_000L
