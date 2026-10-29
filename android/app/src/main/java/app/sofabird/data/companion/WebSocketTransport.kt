package app.sofabird.data.companion

import app.sofabird.data.net.HttpClients
import app.sofabird.data.net.PinningTrustManager
import app.sofabird.protocol.model.TransportKind
import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.http.URLProtocol
import io.ktor.http.path
import io.ktor.websocket.CloseReason
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText
import okhttp3.OkHttpClient

/**
 * `ws://host:47555/ws` or `wss://host:47557/ws`: one JSON message per text frame (a trailing `\n` is tolerated by the
 * codec). An encrypted one has its own HTTP client (the certificate pin is per PC), closed with it.
 */
class WebSocketTransport(
    private val session: DefaultClientWebSocketSession,
    override val certificate: String? = null,
    private val ownClient: HttpClient? = null,
) : CompanionTransport {
    override val kind: TransportKind = TransportKind.WIFI

    @Volatile private var closed = false

    override suspend fun send(text: String) {
        session.send(Frame.Text(text))
    }

    override suspend fun receive(): String? {
        while (!closed) {
            val frame = session.incoming.receiveCatching().getOrNull() ?: return null
            if (frame is Frame.Text) return frame.readText()
        }
        return null
    }

    override suspend fun close() {
        closed = true
        runCatching { session.close(CloseReason(CloseReason.Codes.NORMAL, "bye")) }
        ownClient?.close()
    }

    companion object {
        const val WS_PATH = "/ws"

        suspend fun open(client: HttpClient, host: String, port: Int): WebSocketTransport =
            WebSocketTransport(session(client, URLProtocol.WS, host, port))

        /** `wss` trusting only [pin] (or, without one, the certificate shown, which [certificate] then tells). */
        suspend fun openTls(base: OkHttpClient, host: String, port: Int, pin: String?): WebSocketTransport {
            val trust = PinningTrustManager(pin)
            val client = HttpClients.ktor(
                base.newBuilder().sslSocketFactory(trust.socketFactory(), trust).hostnameVerifier(PinningTrustManager.anyHost).build(),
            )
            return try {
                val session = session(client, URLProtocol.WSS, host, port)
                WebSocketTransport(session, certificate = trust.seen, ownClient = client)
            } catch (e: Throwable) {
                client.close()
                throw e
            }
        }

        private suspend fun session(client: HttpClient, scheme: URLProtocol, host: String, port: Int) =
            client.webSocketSession {
                url {
                    protocol = scheme
                    this.host = host
                    this.port = port
                    path(WS_PATH)
                }
            }
    }
}
