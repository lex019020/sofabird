package app.sofabird.data.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import app.sofabird.protocol.companion.COMPANION_SERVICE_TYPE
import app.sofabird.protocol.companion.COMPANION_WS_PORT
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import java.net.Inet4Address
import kotlin.coroutines.resume

sealed class NsdEvent {
    data class Resolved(val host: DiscoveredHost, val serviceName: String) : NsdEvent()
    data class Lost(val serviceName: String) : NsdEvent()
}

/**
 * DNS-SD browse for `_sofabird._tcp`. One discovery at a time, services resolved sequentially
 * (NsdManager fails with FAILURE_ALREADY_ACTIVE on concurrent resolves), MulticastLock held for
 * the duration (needed below SDK extension T-7; harmless above).
 */
class NsdDiscovery(private val context: Context) {

    fun discover(): Flow<NsdEvent> = callbackFlow {
        val nsd = context.getSystemService(Context.NSD_SERVICE) as? NsdManager
        if (nsd == null) {
            close()
            return@callbackFlow
        }
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val lock = wifi?.createMulticastLock("sofabird-nsd")?.apply {
            setReferenceCounted(false)
            runCatching { acquire() }
        }

        val toResolve = Channel<NsdServiceInfo>(Channel.UNLIMITED)
        val resolver = launch {
            for (info in toResolve) {
                val resolved = resolve(nsd, info) ?: continue
                trySend(resolved)
            }
        }

        val listener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "start discovery failed: $errorCode")
                close()
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "stop discovery failed: $errorCode")
            }

            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                toResolve.trySend(serviceInfo)
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                trySend(NsdEvent.Lost(serviceInfo.serviceName))
            }
        }

        try {
            nsd.discoverServices("$COMPANION_SERVICE_TYPE.", NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: Exception) {
            Log.w(TAG, "discoverServices threw", e)
            close(e)
        }

        awaitClose {
            resolver.cancel()
            toResolve.close()
            runCatching { nsd.stopServiceDiscovery(listener) }
            runCatching { lock?.release() }
        }
    }

    @Suppress("DEPRECATION")
    private suspend fun resolve(nsd: NsdManager, info: NsdServiceInfo): NsdEvent.Resolved? =
        suspendCancellableCoroutine { cont ->
            nsd.resolveService(
                info,
                object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                        Log.d(TAG, "resolve failed for ${serviceInfo.serviceName}: $errorCode")
                        if (cont.isActive) cont.resume(null)
                    }

                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        val address = serviceInfo.host
                        val hostAddress = (address as? Inet4Address)?.hostAddress ?: address?.hostAddress
                        if (hostAddress == null) {
                            if (cont.isActive) cont.resume(null)
                            return
                        }
                        val txt = serviceInfo.attributes.orEmpty().mapValues { (_, v) -> v?.toString(Charsets.UTF_8) ?: "" }
                        val host = DiscoveredHost(
                            host = hostAddress,
                            name = txt["name"]?.takeIf { it.isNotBlank() } ?: serviceInfo.serviceName,
                            wsPort = serviceInfo.port.takeIf { it > 0 } ?: COMPANION_WS_PORT,
                            pcId = txt["id"],
                            sources = setOf(DiscoverySource.NSD),
                        )
                        if (cont.isActive) cont.resume(NsdEvent.Resolved(host, serviceInfo.serviceName))
                    }
                },
            )
        }

    private companion object {
        const val TAG = "NsdDiscovery"
    }
}
