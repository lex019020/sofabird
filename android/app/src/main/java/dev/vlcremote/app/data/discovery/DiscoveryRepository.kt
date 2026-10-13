package dev.vlcremote.app.data.discovery

import android.util.Log
import dev.vlcremote.protocol.companion.BtAddress
import dev.vlcremote.protocol.companion.CompanionCodec
import io.ktor.client.HttpClient
import io.ktor.client.request.get
import io.ktor.client.statement.bodyAsText
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.onCompletion
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Runs the discovery methods together and merges the results by host:
 * NSD browse (continuous), UDP beacon rounds (every 10 s), optional /24 HTTP sweep (on demand).
 * Companion hosts are enriched from `GET /info` (VLC running? pairing needed?).
 */
class DiscoveryRepository(
    private val nsd: NsdDiscovery,
    private val beacon: UdpBeaconDiscovery,
    private val scanner: SubnetScanner,
    private val ktor: HttpClient,
    private val scope: CoroutineScope,
) {
    private val _hosts = MutableStateFlow<Map<String, DiscoveredHost>>(emptyMap())
    val hosts: StateFlow<Map<String, DiscoveredHost>> = _hosts.asStateFlow()

    private val _scanning = MutableStateFlow(false)
    val scanning: StateFlow<Boolean> = _scanning.asStateFlow()

    private val _sweeping = MutableStateFlow(false)
    val sweeping: StateFlow<Boolean> = _sweeping.asStateFlow()

    private var nsdJob: Job? = null
    private var beaconJob: Job? = null
    private var sweepJob: Job? = null
    private val pendingLost = HashMap<String, Job>()
    private val nsdNames = HashMap<String, String>() // serviceName -> host
    private val infoInFlight = HashSet<String>()

    fun start() {
        if (_scanning.value) return
        _scanning.value = true
        nsdJob = scope.launch {
            nsd.discover()
                .catch { Log.w(TAG, "nsd failed: ${it.message}") }
                .collect { event ->
                    when (event) {
                        is NsdEvent.Resolved -> {
                            pendingLost.remove(event.serviceName)?.cancel()
                            nsdNames[event.serviceName] = event.host.host
                            add(event.host)
                        }
                        is NsdEvent.Lost -> {
                            // Debounce: NSD often flaps lost/found within a second.
                            pendingLost[event.serviceName]?.cancel()
                            pendingLost[event.serviceName] = launch {
                                delay(LOST_DEBOUNCE_MS)
                                nsdNames.remove(event.serviceName)?.let { host -> removeSource(host, DiscoverySource.NSD) }
                            }
                        }
                    }
                }
        }
        beaconJob = scope.launch {
            while (isActive) {
                beacon.discover()
                    .catch { Log.d(TAG, "beacon failed: ${it.message}") }
                    .collect { add(it) }
                delay(BEACON_PERIOD_MS)
            }
        }
    }

    fun stop() {
        nsdJob?.cancel(); nsdJob = null
        beaconJob?.cancel(); beaconJob = null
        sweepJob?.cancel(); sweepJob = null
        pendingLost.values.forEach { it.cancel() }
        pendingLost.clear()
        _scanning.value = false
        _sweeping.value = false
    }

    /** One-off /24 sweep for port 8080. */
    fun sweepSubnet() {
        if (sweepJob?.isActive == true) return
        _sweeping.value = true
        sweepJob = scope.launch {
            scanner.scan()
                .catch { Log.d(TAG, "sweep failed: ${it.message}") }
                .onCompletion { _sweeping.value = false }
                .collect { add(it) }
        }
    }

    private fun add(host: DiscoveredHost) {
        _hosts.update { map ->
            val merged = map[host.host]?.merge(host) ?: host
            map + (host.host to merged)
        }
        // `/info` once, then again every few beacon rounds so "VLC running / no control" stays fresh.
        val known = _hosts.value[host.host] ?: host
        val now = System.currentTimeMillis()
        if (known.hasCompanion && now - known.infoAt > INFO_REFRESH_MS && infoInFlight.add(host.host)) {
            scope.launch {
                try {
                    enrichFromInfo(known)
                } finally {
                    infoInFlight.remove(host.host)
                }
            }
        }
    }

    private fun removeSource(hostAddress: String, source: DiscoverySource) {
        _hosts.update { map ->
            val existing = map[hostAddress] ?: return@update map
            val remaining = existing.sources - source
            if (remaining.isEmpty()) map - hostAddress else map + (hostAddress to existing.copy(sources = remaining))
        }
    }

    private suspend fun enrichFromInfo(host: DiscoveredHost) {
        val info = withTimeoutOrNull(INFO_TIMEOUT_MS) {
            runCatching {
                val body = ktor.get("http://${host.host}:${host.wsPort}/info").bodyAsText()
                CompanionCodec.decodeInfo(body)
            }.getOrNull()
        } ?: return
        _hosts.update { map ->
            val existing = map[host.host] ?: return@update map
            map + (
                host.host to existing.copy(
                    name = info.name.ifBlank { existing.name },
                    needsPairing = info.needsPairing,
                    vlcRunning = info.vlc?.running,
                    vlcVersion = info.vlc?.version ?: existing.vlcVersion,
                    vlcStatus = info.vlc?.effectiveStatus,
                    infoAt = System.currentTimeMillis(),
                    wsPort = info.wsPort,
                    bt = BtAddress.normalize(info.bt) ?: existing.bt,
                )
                )
        }
    }

    private companion object {
        const val TAG = "Discovery"
        const val LOST_DEBOUNCE_MS = 3_000L
        const val BEACON_PERIOD_MS = 10_000L
        const val INFO_REFRESH_MS = 8_000L
        const val INFO_TIMEOUT_MS = 3_000L
    }
}
