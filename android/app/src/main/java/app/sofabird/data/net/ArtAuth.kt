package app.sofabird.data.net

import okhttp3.Interceptor
import java.util.concurrent.ConcurrentHashMap

/**
 * Supplies `Authorization` for artwork requests made by Coil: Basic for VLC's `/art`,
 * Bearer for the companion's `/art` proxy. Keyed by `host:port` of the active connection.
 */
class ArtAuth {
    private val headers = ConcurrentHashMap<String, String>()

    fun set(host: String, port: Int, authorization: String?) {
        val key = "$host:$port"
        if (authorization == null) headers.remove(key) else headers[key] = authorization
    }

    fun clear() = headers.clear()

    val interceptor: Interceptor = Interceptor { chain ->
        val request = chain.request()
        val auth = headers["${request.url.host}:${request.url.port}"]
        if (auth == null || request.header("Authorization") != null) {
            chain.proceed(request)
        } else {
            chain.proceed(request.newBuilder().header("Authorization", auth).build())
        }
    }
}
