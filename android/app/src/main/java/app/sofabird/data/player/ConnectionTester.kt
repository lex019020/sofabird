package app.sofabird.data.player

import app.sofabird.data.companion.CompanionHandshakeException
import app.sofabird.data.companion.CompanionTransport
import app.sofabird.data.companion.CompanionTransports
import app.sofabird.data.companion.classifyCompanionError
import app.sofabird.data.companion.handshake
import app.sofabird.protocol.companion.BtAddress
import app.sofabird.protocol.companion.CompanionMessage
import app.sofabird.protocol.companion.DeviceInfo
import app.sofabird.protocol.companion.lanRoute
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.VlcInfo
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.cancellation.CancellationException

sealed class TestResult {
    data class CompanionOk(
        val name: String,
        val pcId: String?,
        val token: String?,
        val capabilities: Capabilities,
        val vlc: VlcInfo,
        /** PC Bluetooth address from `welcome`, normalized. */
        val bt: String? = null,
        /**
         * The companion's encrypted WebSocket: its port and, after a LAN test, the certificate it showed (to pin).
         * Over Bluetooth only the port the companion names; `null` when it has none.
         */
        val tlsPort: Int? = null,
        val certPin: String? = null,
    ) : TestResult()

    data class Failed(val error: ConnectionError) : TestResult()
}

/** One `hello` to a companion for the pairing flow: a precise verdict, the link is not kept. */
class ConnectionTester(private val transports: CompanionTransports) {

    /**
     * One `hello` over the encrypted LAN WebSocket ([lanRoute]: a known [certPin] is required, without one the
     * certificate shown is trusted); the link is closed afterwards. A success carries the certificate to pin.
     */
    suspend fun testCompanion(
        host: String,
        token: String?,
        pairCode: String?,
        device: DeviceInfo,
        tlsPort: Int? = null,
        certPin: String? = null,
    ): TestResult {
        val route = lanRoute(tlsPort, certPin)
        val result = helloOver(token, pairCode, device, timeoutMs = LAN_HELLO_TIMEOUT_MS) { transports.openWebSocket(host, route) }
        return if (result is TestResult.CompanionOk) result.copy(tlsPort = route.port) else result
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
                        // Over Bluetooth: where the LAN link will be; the first session there pins the certificate.
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

/** A LAN `hello` for pairing: connecting (the TLS handshake included) and the companion's answer. */
private const val LAN_HELLO_TIMEOUT_MS = 12_000L
