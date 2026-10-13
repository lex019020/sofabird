package dev.vlcremote.app.data.vlc

import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.vlc.VlcLuaErrorException
import dev.vlcremote.protocol.vlc.VlcParseException
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

/** Maps transport failures and HTTP codes to the user-facing [ConnectionError] classes. */
object VlcErrorClassifier {

    fun fromStatusCode(code: Int): ConnectionError? = when (code) {
        HttpURLConnection.HTTP_UNAUTHORIZED -> ConnectionError.WrongPassword
        HttpURLConnection.HTTP_FORBIDDEN -> ConnectionError.NoPasswordSet
        else -> null
    }

    /** The first recognisable failure in the cause chain (8 levels at most). */
    fun fromThrowable(t: Throwable): ConnectionError {
        if (t is VlcHttpException) return t.error
        return generateSequence(t) { it.cause }.take(MAX_CAUSE_DEPTH).firstNotNullOfOrNull(::classify)
            ?: ConnectionError.Other(t.message ?: t::class.java.simpleName)
    }

    private const val MAX_CAUSE_DEPTH = 8

    private fun classify(cur: Throwable): ConnectionError? {
        val message = cur.message.orEmpty()
        return when {
            cur is VlcLuaErrorException -> ConnectionError.LuaError(cur.detail)
            cur is VlcParseException -> if (cur.message == "empty body") ConnectionError.EmptyBody else ConnectionError.Other(cur.message)
            cur is UnknownHostException -> ConnectionError.UnknownHost
            cur is ConnectException ->
                if (cur.message?.contains("refused", ignoreCase = true) != false) ConnectionError.Refused else ConnectionError.Timeout
            cur is NoRouteToHostException || cur is SocketTimeoutException -> ConnectionError.Timeout
            cur::class.java.simpleName.contains("Timeout", ignoreCase = true) -> ConnectionError.Timeout
            message.contains("refused", ignoreCase = true) -> ConnectionError.Refused
            message.contains("timed out", ignoreCase = true) -> ConnectionError.Timeout
            message.contains("unreachable", ignoreCase = true) -> ConnectionError.Timeout
            message.contains("No address associated", ignoreCase = true) -> ConnectionError.UnknownHost
            else -> null
        }
    }

    /** Auth errors are not retried automatically — the user has to change the password. */
    fun isAuthError(error: ConnectionError): Boolean =
        error is ConnectionError.WrongPassword || error is ConnectionError.NoPasswordSet
}
