package dev.vlcremote.app.data.discovery

import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.protocol.vlc.VlcCommands
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/**
 * Optional /24 sweep for VLC's HTTP interface: GET `status.json` on every host with ~1 s timeout,
 * 48 in parallel, nearest addresses first. 200/401/403 all mean "VLC is here" (401 = password
 * set, 403 = no password configured).
 */
class SubnetScanner(baseClient: OkHttpClient, private val localNetwork: LocalNetwork) {

    private val client = baseClient.newBuilder()
        .connectTimeout(1, TimeUnit.SECONDS)
        .readTimeout(1, TimeUnit.SECONDS)
        .retryOnConnectionFailure(false)
        .build()

    data class Hit(val host: String, val statusCode: Int)

    fun scan(port: Int = SavedServer.DEFAULT_HTTP_PORT, parallelism: Int = 48): Flow<DiscoveredHost> = channelFlow {
        val candidates = localNetwork.subnet24Candidates()
        val semaphore = Semaphore(parallelism)
        for (host in candidates) {
            launch {
                semaphore.withPermit {
                    probe(host, port)?.let { hit ->
                        send(
                            DiscoveredHost(
                                host = hit.host,
                                name = "",
                                hasCompanion = false,
                                httpPort = port,
                                vlcRunning = true,
                                sources = setOf(DiscoverySource.SUBNET_SWEEP),
                            ),
                        )
                    }
                }
            }
        }
    }.flowOn(Dispatchers.IO)

    /** Also used for a single-host check ("VLC answers here?"). */
    fun probe(host: String, port: Int): Hit? {
        val request = Request.Builder().url("http://$host:$port${VlcCommands.STATUS_PATH}").get().build()
        return try {
            client.newCall(request).execute().use { response ->
                if (response.code in VLC_CODES) Hit(host, response.code) else null
            }
        } catch (_: Exception) {
            null
        }
    }

    private companion object {
        val VLC_CODES = setOf(200, 401, 403)
    }
}
