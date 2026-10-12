package dev.vlcremote.app.data.net

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
    val stripConnectionHeader = Interceptor { chain ->
        val request = chain.request().newBuilder().removeHeader("Connection").build()
        chain.proceed(request)
    }

    fun okHttp(authInterceptor: Interceptor? = null): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .writeTimeout(8, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .apply { authInterceptor?.let { addInterceptor(it) } }
        .addNetworkInterceptor(stripConnectionHeader)
        .build()

    fun ktor(okHttpClient: OkHttpClient): HttpClient = HttpClient(OkHttp) {
        engine { preconfigured = okHttpClient }
        install(WebSockets)
        install(HttpTimeout) {
            connectTimeoutMillis = 3_000
            requestTimeoutMillis = 8_000
            socketTimeoutMillis = 8_000
        }
        expectSuccess = false
    }
}
