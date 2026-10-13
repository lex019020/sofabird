package dev.vlcremote.app.data.companion

import dev.vlcremote.protocol.companion.CompanionCodec
import dev.vlcremote.protocol.companion.CompanionErrorException
import dev.vlcremote.protocol.companion.CompanionMessage
import dev.vlcremote.protocol.companion.DecodedMessage
import dev.vlcremote.protocol.companion.PROTOCOL_VERSION
import dev.vlcremote.protocol.companion.toHandshakeError
import dev.vlcremote.protocol.model.ConnectionError
import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.http.URLProtocol
import io.ktor.http.path
import io.ktor.websocket.CloseReason
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText
import kotlinx.coroutines.withTimeout

/** Credentials for `hello`: either a stored token or a fresh 6-digit pairing code. */
data class CompanionCredentials(val token: String? = null, val pairCode: String? = null)

/** Handshake failed because the companion refused us or speaks another protocol. */
class CompanionHandshakeException(val error: ConnectionError, message: String? = null) : Exception(message)

/** Low-level helpers shared by [CompanionBackend] and the connection tester. */
object CompanionSocket {
    const val WS_PATH = "/ws"
    const val HANDSHAKE_TIMEOUT_MS = 10_000L

    suspend fun open(client: HttpClient, host: String, port: Int): DefaultClientWebSocketSession =
        client.webSocketSession {
            url {
                protocol = URLProtocol.WS
                this.host = host
                this.port = port
                path(WS_PATH)
            }
        }

    suspend fun DefaultClientWebSocketSession.sendMessage(message: CompanionMessage) {
        send(Frame.Text(CompanionCodec.encode(message)))
    }

    /** Reads frames until the next decodable message; `null` when the socket closed. */
    suspend fun DefaultClientWebSocketSession.receiveMessage(): DecodedMessage? {
        while (true) {
            val frame = incoming.receiveCatching().getOrNull() ?: return null
            if (frame !is Frame.Text) continue
            val decoded = CompanionCodec.decode(frame.readText()) ?: continue
            return decoded
        }
    }

    /**
     * Sends `hello` and waits for `welcome`. Throws [CompanionHandshakeException] on `err`,
     * protocol mismatch or a closed socket.
     */
    suspend fun DefaultClientWebSocketSession.handshake(hello: CompanionMessage.Hello): CompanionMessage.Welcome {
        sendMessage(hello)
        return withTimeout(HANDSHAKE_TIMEOUT_MS) {
            while (true) {
                when (val decoded = receiveMessage()) {
                    null -> throw CompanionHandshakeException(ConnectionError.Other("socket closed during handshake"))
                    is DecodedMessage.Unknown -> continue
                    is DecodedMessage.Known -> when (val msg = decoded.message) {
                        is CompanionMessage.Welcome -> {
                            if (msg.v != PROTOCOL_VERSION) {
                                throw CompanionHandshakeException(ConnectionError.ProtocolMismatch(msg.v), "protocol ${msg.v}")
                            }
                            return@withTimeout msg
                        }
                        is CompanionMessage.Err -> throw CompanionHandshakeException(
                            msg.toHandshakeError(sentPairCode = hello.pairCode != null),
                            msg.msg,
                        )
                        else -> continue // e.g. an early `state`
                    }
                }
            }
            @Suppress("UNREACHABLE_CODE")
            error("unreachable")
        }
    }

    suspend fun DefaultClientWebSocketSession.closeQuietly() {
        runCatching { close(CloseReason(CloseReason.Codes.NORMAL, "bye")) }
    }

    fun classify(t: Throwable): ConnectionError = when (t) {
        is CompanionHandshakeException -> t.error
        is CompanionErrorException -> ConnectionError.Other(t.message)
        else -> dev.vlcremote.app.data.vlc.VlcErrorClassifier.fromThrowable(t)
    }
}
