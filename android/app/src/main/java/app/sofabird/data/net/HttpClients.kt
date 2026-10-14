package app.sofabird.data.net

import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.websocket.WebSockets
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/**
 * Shared OkHttp/Ktor clients. The network interceptor strips the `Connection` header that OkHttp
 * adds (`Keep-Alive`): VLC's HTTP server closes the connection after every request when the
 * client sends any `Connection` header at all.
 */
object HttpClients {
    private const val CONNECT_TIMEOUT_MS = 3_000L
    private const val IO_TIMEOUT_MS = 8_000L

    val stripConnectionHeader = Interceptor { chain ->
        val request = chain.request().newBuilder().removeHeader("Connection").build()
        chain.proceed(request)
    }

    fun okHttp(authInterceptor: Interceptor? = null): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(CONNECT_TIMEOUT_MS, TimeUnit.MILLISECONDS)
        .readTimeout(IO_TIMEOUT_MS, TimeUnit.MILLISECONDS)
        .writeTimeout(IO_TIMEOUT_MS, TimeUnit.MILLISECONDS)
        .retryOnConnectionFailure(true)
        .apply { authInterceptor?.let { addInterceptor(it) } }
        .addNetworkInterceptor(stripConnectionHeader)
        .build()

    fun ktor(okHttpClient: OkHttpClient): HttpClient = HttpClient(OkHttp) {
        engine { preconfigured = okHttpClient }
        install(WebSockets)
        install(HttpTimeout) {
            connectTimeoutMillis = CONNECT_TIMEOUT_MS
            requestTimeoutMillis = IO_TIMEOUT_MS
            socketTimeoutMillis = IO_TIMEOUT_MS
        }
        expectSuccess = false
    }
}
