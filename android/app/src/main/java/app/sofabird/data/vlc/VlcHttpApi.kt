package app.sofabird.data.vlc

import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.vlc.VlcCommands
import app.sofabird.protocol.vlc.VlcErrorPage
import io.ktor.client.HttpClient
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.statement.bodyAsText
import io.ktor.http.HttpHeaders
import java.util.Base64
import java.util.concurrent.TimeUnit
import kotlin.coroutines.cancellation.CancellationException

/** A classified failure of a VLC HTTP request. */
class VlcHttpException(val error: ConnectionError, val statusCode: Int? = null, cause: Throwable? = null) :
    Exception("VLC HTTP error: $error" + (statusCode?.let { " (HTTP $it)" } ?: ""), cause)

data class VlcHttpResponse(val body: String, val latencyMs: Long)

/**
 * Thin typed wrapper over the VLC `requests/….json` pages. Basic auth with an empty user name.
 * All failures surface as [VlcHttpException] with a [ConnectionError] the UI can explain.
 */
class VlcHttpApi(
    private val client: HttpClient,
    val host: String,
    val port: Int,
    password: String,
) {
    val baseUrl: String = "http://$host:$port"
    private val authorization: String =
        "Basic " + Base64.getEncoder().encodeToString(":$password".toByteArray(Charsets.UTF_8))

    /** `status.json`, optionally with a `command=…` query (returns the fresh status). */
    suspend fun status(commandQuery: String? = null): VlcHttpResponse =
        get(VlcCommands.STATUS_PATH, commandQuery)

    suspend fun playlist(): VlcHttpResponse = get(VlcCommands.PLAYLIST_PATH, null)

    suspend fun browse(uri: String): VlcHttpResponse = get(VlcCommands.BROWSE_PATH, VlcCommands.browse(uri))

    /** Level-1 Lua page; 404 means level 0. */
    suspend fun tracks(): VlcHttpResponse = get(VlcCommands.TRACKS_PATH, null)

    fun artUrl(plId: Int?): String = if (plId != null) "$baseUrl${VlcCommands.ART_PATH}?item=$plId" else "$baseUrl${VlcCommands.ART_PATH}"

    /** Header value Coil needs to load `/art`. */
    fun authorizationHeader(): String = authorization

    private suspend fun get(path: String, query: String?): VlcHttpResponse {
        val url = if (query.isNullOrEmpty()) "$baseUrl$path" else "$baseUrl$path?$query"
        val started = System.nanoTime()
        val response = classifyFailures(statusCode = null) {
            client.get(url) { header(HttpHeaders.Authorization, authorization) }
        }
        val latency = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - started)
        val code = response.status.value
        checkStatus(code)
        val body = classifyFailures(code) { response.bodyAsText() }
        checkBody(body, code)
        return VlcHttpResponse(body, latency)
    }

    /** A network step: any failure but cancellation becomes a [VlcHttpException] with a user-facing class. */
    private inline fun <T> classifyFailures(statusCode: Int?, step: () -> T): T = try {
        step()
    } catch (e: CancellationException) {
        throw e
    } catch (e: Throwable) {
        throw VlcHttpException(VlcErrorClassifier.fromThrowable(e), statusCode, e)
    }

    private fun checkStatus(code: Int) {
        VlcErrorClassifier.fromStatusCode(code)?.let { throw VlcHttpException(it, code) }
        if (code / 100 != 2) throw VlcHttpException(ConnectionError.Other("HTTP $code"), code) // not 2xx
    }

    /** VLC answers a broken Lua page with 200 and an HTML error page. */
    private fun checkBody(body: String, code: Int) {
        if (body.isBlank()) throw VlcHttpException(ConnectionError.EmptyBody, code)
        if (VlcErrorPage.isErrorPage(body)) {
            throw VlcHttpException(ConnectionError.LuaError(VlcErrorPage.extractDetail(body)), code)
        }
    }
}
