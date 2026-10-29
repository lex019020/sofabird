package app.sofabird.data.net

import java.net.Socket
import java.security.KeyStore
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509ExtendedTrustManager
import javax.net.ssl.X509TrustManager

/** SHA-256 of a certificate (DER), upper-case hex: what a companion publishes as `tls.sha256`. */
fun X509Certificate.sha256(): String = MessageDigest.getInstance("SHA-256").digest(encoded).joinToString("") { "%02X".format(it) }

/** The companion showed another certificate than the pinned one. */
class CertificateChangedException(expected: String, actual: String) :
    CertificateException("certificate changed: pinned $expected, got $actual")

/**
 * Trusts a companion's self-signed certificate by its SHA-256 [pin] — no authority, no host name. Without a pin (the
 * first encrypted connection) any certificate is accepted and remembered in [seen], to be pinned once the companion
 * accepted the phone's token or pairing code.
 */
class PinningTrustManager(private val pin: String?) : X509TrustManager {
    @Volatile var seen: String? = null
        private set

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("no certificate")
        val fingerprint = leaf.sha256()
        if (pin != null && !pin.equals(fingerprint, ignoreCase = true)) throw CertificateChangedException(pin, fingerprint)
        seen = fingerprint
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("server only")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun socketFactory(): SSLSocketFactory =
        SSLContext.getInstance("TLS").apply { init(null, arrayOf(this@PinningTrustManager), null) }.socketFactory

    companion object {
        /** The pin, not the name, identifies the PC (its address changes; the certificate names no address). */
        val anyHost = HostnameVerifier { _, _ -> true }
    }
}

/**
 * The pinned companions of the shared HTTP client (artwork over https: `host:port` → SHA-256). Any other server —
 * TMDB, YouTube thumbnails, GitHub — is checked by the system as usual.
 */
class PinnedServers {
    private val pins = ConcurrentHashMap<String, String>()
    private val system: X509TrustManager = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        .apply { init(null as KeyStore?) }
        .trustManagers.filterIsInstance<X509TrustManager>().first()
    private val systemNames: HostnameVerifier = HttpsURLConnection.getDefaultHostnameVerifier()

    fun set(host: String, port: Int, sha256: String?) {
        val key = "$host:$port"
        if (sha256 == null) pins.remove(key) else pins[key] = sha256
    }

    private fun pinFor(host: String?, port: Int): String? = host?.let { pins["$it:$port"] }

    val trustManager: X509TrustManager = object : X509ExtendedTrustManager() {
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) {
            val host = (socket as? SSLSocket)?.handshakeSession?.peerHost ?: socket?.inetAddress?.hostAddress
            check(chain, authType, pinFor(host, socket?.port ?: -1)) {
                if (system is X509ExtendedTrustManager) {
                    system.checkServerTrusted(chain, authType, socket)
                } else {
                    system.checkServerTrusted(chain, authType)
                }
            }
        }

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) {
            check(chain, authType, pinFor(engine?.peerHost, engine?.peerPort ?: -1)) {
                if (system is X509ExtendedTrustManager) {
                    system.checkServerTrusted(chain, authType, engine)
                } else {
                    system.checkServerTrusted(chain, authType)
                }
            }
        }

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = system.checkServerTrusted(chain, authType)

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) =
            throw CertificateException("server only")

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) =
            throw CertificateException("server only")

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("server only")

        override fun getAcceptedIssuers(): Array<X509Certificate> = system.acceptedIssuers

        private inline fun check(chain: Array<out X509Certificate>?, authType: String?, pin: String?, otherwise: () -> Unit) {
            if (pin == null) otherwise() else PinningTrustManager(pin).checkServerTrusted(chain, authType)
        }
    }

    val hostnameVerifier = HostnameVerifier { host, session ->
        pinFor(host, session.peerPort) != null || systemNames.verify(host, session)
    }

    fun socketFactory(): SSLSocketFactory = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trustManager), null) }.socketFactory
}
