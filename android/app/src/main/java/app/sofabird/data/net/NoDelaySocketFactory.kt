package app.sofabird.data.net

import java.net.InetAddress
import java.net.Socket
import javax.net.SocketFactory

/**
 * Sockets with `TCP_NODELAY`: a small message goes out at once. OkHttp leaves Nagle's algorithm on, and with it a
 * stream of small WebSocket frames (the touchpad's moves, ~100 bytes each) waits for the PC to acknowledge the previous
 * one — and Windows acknowledges a lone segment only after up to 200 ms, so the pointer moved in jerks a few times a
 * second.
 */
class NoDelaySocketFactory(private val delegate: SocketFactory = getDefault()) : SocketFactory() {
    override fun createSocket(): Socket = delegate.createSocket().noDelay()

    override fun createSocket(host: String?, port: Int): Socket = delegate.createSocket(host, port).noDelay()

    override fun createSocket(host: String?, port: Int, localHost: InetAddress?, localPort: Int): Socket =
        delegate.createSocket(host, port, localHost, localPort).noDelay()

    override fun createSocket(host: InetAddress?, port: Int): Socket = delegate.createSocket(host, port).noDelay()

    override fun createSocket(address: InetAddress?, port: Int, localAddress: InetAddress?, localPort: Int): Socket =
        delegate.createSocket(address, port, localAddress, localPort).noDelay()

    private fun Socket.noDelay(): Socket = apply { tcpNoDelay = true }
}
