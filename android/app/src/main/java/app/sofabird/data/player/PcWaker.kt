package app.sofabird.data.player

import android.util.Log
import app.sofabird.data.discovery.LocalNetwork
import app.sofabird.protocol.companion.MagicPacket
import app.sofabird.protocol.model.ConnectionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * "Wake up the PC" (Wake-on-LAN): sends the magic packet for every adapter the companion named (`welcome.macs`) as a
 * broadcast on the phone's network, then reconnects until the PC answers or [WAIT_MS] pass (it is sent again on the
 * way: the first ones may go out before the phone's Wi-Fi is fully up).
 */
class PcWaker(
    private val controller: PlayerController,
    private val localNetwork: LocalNetwork,
    private val scope: CoroutineScope,
) {
    enum class State { IDLE, WAKING, FAILED }

    private val _state = MutableStateFlow(State.IDLE)
    val state: StateFlow<State> = _state.asStateFlow()
    private var job: Job? = null

    fun wake() {
        val server = controller.activeServer.value ?: return
        val packets = server.wakeMacs.mapNotNull(MagicPacket::of)
        if (packets.isEmpty() || job?.isActive == true) return
        job = scope.launch {
            _state.value = State.WAKING
            val woke = withTimeoutOrNull(WAIT_MS) {
                var sentAt = 0L
                while (!controller.connection.value.isOnline) {
                    if (System.currentTimeMillis() - sentAt >= RESEND_MS) {
                        send(packets)
                        sentAt = System.currentTimeMillis()
                    }
                    delay(RETRY_MS)
                    // A woken PC needs a few seconds for its network and the companion: knock again.
                    if (controller.connection.value is ConnectionState.Disconnected) controller.autoConnectLast() else controller.retryNow()
                }
                true
            }
            _state.value = if (woke == true) State.IDLE else State.FAILED
            Log.i(TAG, if (woke == true) "${server.displayName} woke up" else "${server.displayName} did not wake up")
        }
    }

    private suspend fun send(packets: List<ByteArray>) = withContext(Dispatchers.IO) {
        val targets = buildList {
            add(InetAddress.getByName("255.255.255.255"))
            localNetwork.directedBroadcast()?.let { add(it) }
        }
        runCatching {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                repeat(ROUNDS) {
                    for (packet in packets) for (target in targets) for (port in MagicPacket.PORTS) {
                        runCatching { socket.send(DatagramPacket(packet, packet.size, target, port)) }
                            .onFailure { Log.d(TAG, "send to $target:$port failed: ${it.message}") }
                    }
                }
            }
        }.onFailure { Log.w(TAG, "Wake-on-LAN failed", it) }
    }

    private companion object {
        const val TAG = "PcWaker"
        const val WAIT_MS = 90_000L
        const val RESEND_MS = 15_000L
        const val RETRY_MS = 4_000L
        const val ROUNDS = 3
    }
}
