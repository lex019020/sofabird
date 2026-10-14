package app.sofabird.data.companion

import android.util.Log
import app.sofabird.data.discovery.UdpBeaconDiscovery
import app.sofabird.protocol.companion.BtAddress
import app.sofabird.protocol.companion.CompanionCodec
import io.ktor.client.HttpClient
import io.ktor.client.request.get
import io.ktor.client.statement.bodyAsText
import io.ktor.http.isSuccess
import kotlinx.coroutines.flow.firstOrNull
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.cancellation.CancellationException

/** [CompanionTransports] over Ktor (WebSocket, `/info`), the UDP beacon and Android Bluetooth. */
class AndroidCompanionTransports(
    private val ktor: HttpClient,
    private val bluetooth: BluetoothSupport,
    private val beacon: UdpBeaconDiscovery,
) : CompanionTransports {

    override suspend fun openWebSocket(host: String, port: Int): CompanionTransport =
        WebSocketTransport.open(ktor, host, port)

    override fun bluetoothStatus(address: String?): BluetoothStatus = bluetooth.status(address)

    override suspend fun openRfcomm(address: String): CompanionTransport {
        val adapter = bluetooth.adapter ?: throw BluetoothConnectException("no Bluetooth adapter")
        val device = bluetooth.remoteDevice(address) ?: throw BluetoothConnectException("bad address $address")
        return RfcommTransport.connect(adapter, device, cancelDiscovery = bluetooth.mayCancelDiscovery)
    }

    override suspend fun probeLan(host: String, port: Int, pcId: String?, btAddress: String?): LanEndpoint? {
        if (host.isNotBlank()) {
            val info = withTimeoutOrNull(INFO_TIMEOUT_MS) {
                try {
                    val response = ktor.get("http://$host:$port/info")
                    if (response.status.isSuccess()) CompanionCodec.decodeInfo(response.bodyAsText()) else null
                } catch (e: CancellationException) {
                    throw e
                } catch (ignored: Exception) {
                    null // not a companion (or not reachable): the probe just fails
                }
            }
            if (info != null) return LanEndpoint(host, info.wsPort, BtAddress.normalize(info.bt))
        }
        if (pcId == null && btAddress == null) return null
        // The PC may have a new address (DHCP) or we never knew it (paired over Bluetooth).
        val found = try {
            beacon.discover(windowMs = BEACON_WINDOW_MS, sends = 2).firstOrNull { h ->
                (pcId != null && h.pcId == pcId) || BtAddress.same(h.bt, btAddress)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.d(TAG, "beacon probe failed: ${e.message}")
            null
        } ?: return null
        return LanEndpoint(found.host, found.wsPort, found.bt)
    }

    private companion object {
        const val TAG = "CompanionTransports"
        const val INFO_TIMEOUT_MS = 2_000L
        const val BEACON_WINDOW_MS = 1_500L
    }
}
