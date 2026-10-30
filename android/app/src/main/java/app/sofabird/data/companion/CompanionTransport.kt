package app.sofabird.data.companion

import app.sofabird.protocol.companion.CompanionCodec
import app.sofabird.protocol.companion.CompanionMessage
import app.sofabird.protocol.companion.DecodedMessage
import app.sofabird.protocol.companion.LanRoute
import app.sofabird.protocol.companion.PROTOCOL_VERSION
import app.sofabird.protocol.companion.toHandshakeError
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.TransportKind
import kotlinx.coroutines.withTimeout

/**
 * One link to the companion carrying protocol messages as text: a WebSocket (one message per
 * frame) or an RFCOMM byte stream (NDJSON). Same messages, handshake and token on both.
 * [receive] is called from a single reader coroutine; [send] may be called from anywhere.
 */
interface CompanionTransport {
    val kind: TransportKind

    /** The SHA-256 of the companion's certificate on the encrypted WebSocket; `null` on RFCOMM. */
    val certificate: String? get() = null

    /** Sends one message (a JSON object; framing is the transport's business). */
    suspend fun send(text: String)

    /** The next message text; `null` once the link is closed by either side (also after [close]). */
    suspend fun receive(): String?

    /** Closes the link; a pending [receive] returns `null`. Never throws. */
    suspend fun close()
}

/** Credentials for `hello`: either a stored token or a fresh 6-digit pairing code. */
data class CompanionCredentials(val token: String? = null, val pairCode: String? = null)

/** Handshake failed because the companion refused us or speaks another protocol. */
class CompanionHandshakeException(val error: ConnectionError, message: String? = null) : Exception(message ?: error.toString())

/** Handshake errors that the same companion will repeat on any link: no point in retrying or falling back. */
val ConnectionError.isTerminalHandshakeError: Boolean
    get() = this is ConnectionError.PairingRequired ||
        this is ConnectionError.InvalidPairCode ||
        this is ConnectionError.ProtocolMismatch ||
        this is ConnectionError.CertificateChanged

const val HANDSHAKE_TIMEOUT_MS = 10_000L

suspend fun CompanionTransport.sendMessage(message: CompanionMessage) = send(CompanionCodec.encode(message))

/** Reads until the next decodable message; `null` when the link closed. */
suspend fun CompanionTransport.receiveMessage(): DecodedMessage? {
    while (true) {
        val text = receive() ?: return null
        return CompanionCodec.decode(text) ?: continue
    }
}

/**
 * Sends `hello` and waits for `welcome`. Throws [CompanionHandshakeException] on `err`,
 * protocol mismatch or a closed link.
 */
suspend fun CompanionTransport.handshake(
    hello: CompanionMessage.Hello,
    timeoutMs: Long = HANDSHAKE_TIMEOUT_MS,
): CompanionMessage.Welcome {
    sendMessage(hello)
    return withTimeout(timeoutMs) {
        var welcome: CompanionMessage.Welcome? = null
        while (welcome == null) {
            when (val decoded = receiveMessage()) {
                null -> throw CompanionHandshakeException(ConnectionError.Other("closed during handshake"))
                is DecodedMessage.Unknown -> Unit
                is DecodedMessage.Known -> when (val msg = decoded.message) {
                    is CompanionMessage.Welcome -> {
                        if (msg.v != PROTOCOL_VERSION) {
                            throw CompanionHandshakeException(ConnectionError.ProtocolMismatch(msg.v), "protocol ${msg.v}")
                        }
                        welcome = msg
                    }
                    is CompanionMessage.Err -> throw CompanionHandshakeException(
                        msg.toHandshakeError(sentPairCode = hello.pairCode != null),
                        msg.msg,
                    )
                    else -> Unit // e.g. an early `state`
                }
            }
        }
        welcome
    }
}

/** Opens links to a companion. Real implementation: [AndroidCompanionTransports]; fakes in tests. */
interface CompanionTransports {
    /**
     * The encrypted WebSocket `wss://host:port/ws` trusting only the pinned certificate ([LanRoute]). Throws when the
     * PC is unreachable; a certificate other than the pinned one is [app.sofabird.data.net.CertificateChangedException].
     */
    suspend fun openWebSocket(host: String, route: LanRoute): CompanionTransport

    /** Whether RFCOMM to [address] can be tried right now. Cheap, no I/O. */
    fun bluetoothStatus(address: String?): BluetoothStatus

    /** RFCOMM to the companion service on [address]. Throws when the connection fails. */
    suspend fun openRfcomm(address: String): CompanionTransport

    /**
     * Is the companion reachable over the LAN? Asks the plain `/info` on the known [host]:[port] first; without a
     * host (or when it does not answer) runs a short UDP beacon round looking for [pcId] / [btAddress].
     * Returns where it answered, or `null`.
     */
    suspend fun probeLan(host: String, port: Int, pcId: String?, btAddress: String?): LanEndpoint?
}

/** Where the companion answered on the LAN ([port]: the plain one, `/info`). */
data class LanEndpoint(val host: String, val port: Int, val btAddress: String? = null)

/** Thrown by RFCOMM when the connection to the PC could not be established. */
class BluetoothConnectException(message: String, cause: Throwable? = null) : java.io.IOException(message, cause)

/** User-facing class of a companion connection failure. */
fun classifyCompanionError(t: Throwable): ConnectionError = when {
    t.isCertificateChanged -> ConnectionError.CertificateChanged
    else -> classifyOther(t)
}

/** Somewhere in the chain (OkHttp wraps it in an SSL exception). */
val Throwable.isCertificateChanged: Boolean
    get() = generateSequence(this) { it.cause }.take(8).any { it is app.sofabird.data.net.CertificateChangedException }

private fun classifyOther(t: Throwable): ConnectionError = when (t) {
    is CompanionHandshakeException -> t.error
    is BluetoothConnectException -> ConnectionError.BluetoothFailed
    is app.sofabird.protocol.companion.CompanionErrorException -> ConnectionError.Other(t.message)
    else -> app.sofabird.data.net.classifyNetworkError(t)
}
