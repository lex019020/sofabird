package dev.vlcremote.app.data.companion

import dev.vlcremote.protocol.model.TransportKind
import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.http.URLProtocol
import io.ktor.http.path
import io.ktor.websocket.CloseReason
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText

/** `ws://host:47555/ws`: one JSON message per text frame (a trailing `\n` is tolerated by the codec). */
class WebSocketTransport(private val session: DefaultClientWebSocketSession) : CompanionTransport {
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
    }

    companion object {
        const val WS_PATH = "/ws"

        suspend fun open(client: HttpClient, host: String, port: Int): WebSocketTransport =
            WebSocketTransport(
                client.webSocketSession {
                    url {
                        protocol = URLProtocol.WS
                        this.host = host
                        this.port = port
                        path(WS_PATH)
                    }
                },
            )
    }
}
