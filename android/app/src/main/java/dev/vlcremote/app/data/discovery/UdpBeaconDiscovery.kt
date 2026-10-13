package dev.vlcremote.app.data.discovery

import android.util.Log
import dev.vlcremote.protocol.companion.BEACON_REQUEST
import dev.vlcremote.protocol.companion.BtAddress
import dev.vlcremote.protocol.companion.COMPANION_BEACON_PORT
import dev.vlcremote.protocol.companion.CompanionCodec
import dev.vlcremote.protocol.companion.CompanionMessage
import dev.vlcremote.protocol.companion.DecodedMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * Sends `VLCREMOTE_DISCOVER 1` to 255.255.255.255:47556 (and the subnet-directed broadcast when
 * known) a few times and collects `beacon` answers for [windowMs].
 */
class UdpBeaconDiscovery(private val localNetwork: LocalNetwork) {

    fun discover(windowMs: Long = 3_000, sends: Int = 3): Flow<DiscoveredHost> = flow {
        val socket = DatagramSocket().apply {
            broadcast = true
            soTimeout = 400
        }
        socket.use { s ->
            val payload = BEACON_REQUEST.toByteArray(Charsets.UTF_8)
            val targets = buildList {
                add(InetAddress.getByName("255.255.255.255"))
                localNetwork.directedBroadcast()?.let { add(it) }
            }
            val deadline = System.currentTimeMillis() + windowMs
            val sendEvery = windowMs / sends
            var nextSend = 0L
            val seen = HashSet<String>()
            val buffer = ByteArray(2048)
            while (System.currentTimeMillis() < deadline) {
                val now = System.currentTimeMillis()
                if (now >= nextSend) {
                    for (t in targets) {
                        runCatching { s.send(DatagramPacket(payload, payload.size, t, COMPANION_BEACON_PORT)) }
                            .onFailure { Log.d(TAG, "send to $t failed: ${it.message}") }
                    }
                    nextSend = now + sendEvery
                }
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    s.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                val decoded = runCatching { CompanionCodec.decode(text) }.getOrNull() as? DecodedMessage.Known ?: continue
                val beacon = decoded.message as? CompanionMessage.Beacon ?: continue
                val host = packet.address.hostAddress ?: continue
                if (!seen.add(host)) continue
                emit(
                    DiscoveredHost(
                        host = host,
                        name = beacon.name,
                        hasCompanion = true,
                        wsPort = beacon.wsPort,
                        needsPairing = beacon.needsPairing,
                        pcId = beacon.pcId,
                        bt = BtAddress.normalize(beacon.bt),
                        sources = setOf(DiscoverySource.UDP_BEACON),
                    ),
                )
            }
        }
    }.flowOn(Dispatchers.IO)

    private companion object {
        const val TAG = "UdpBeacon"
    }
}
