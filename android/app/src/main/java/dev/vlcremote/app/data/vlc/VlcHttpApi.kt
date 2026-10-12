package dev.vlcremote.app.data.vlc

import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.vlc.VlcCommands
import dev.vlcremote.protocol.vlc.VlcErrorPage
import io.ktor.client.HttpClient
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.statement.bodyAsText
import io.ktor.http.HttpHeaders
import java.util.Base64
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
        val response = try {
            client.get(url) { header(HttpHeaders.Authorization, authorization) }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            throw VlcHttpException(VlcErrorClassifier.fromThrowable(e), cause = e)
        }
        val latency = (System.nanoTime() - started) / 1_000_000
        val code = response.status.value
        VlcErrorClassifier.fromStatusCode(code)?.let { throw VlcHttpException(it, code) }
        if (code !in 200..299) {
            throw VlcHttpException(ConnectionError.Other("HTTP $code"), code)
        }
        val body = try {
            response.bodyAsText()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            throw VlcHttpException(VlcErrorClassifier.fromThrowable(e), code, e)
        }
        if (body.isBlank()) throw VlcHttpException(ConnectionError.EmptyBody, code)
        if (VlcErrorPage.isErrorPage(body)) {
            throw VlcHttpException(ConnectionError.LuaError(VlcErrorPage.extractDetail(body)), code)
        }
        return VlcHttpResponse(body, latency)
    }
}
