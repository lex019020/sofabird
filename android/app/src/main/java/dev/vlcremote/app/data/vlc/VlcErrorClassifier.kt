package dev.vlcremote.app.data.vlc

import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.vlc.VlcLuaErrorException
import dev.vlcremote.protocol.vlc.VlcParseException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

/** Maps transport failures and HTTP codes to the user-facing [ConnectionError] classes. */
object VlcErrorClassifier {

    fun fromStatusCode(code: Int): ConnectionError? = when (code) {
        401 -> ConnectionError.WrongPassword
        403 -> ConnectionError.NoPasswordSet
        else -> null
    }

    fun fromThrowable(t: Throwable): ConnectionError {
        if (t is VlcHttpException) return t.error
        var cur: Throwable? = t
        var depth = 0
        while (cur != null && depth < 8) {
            when (cur) {
                is VlcLuaErrorException -> return ConnectionError.LuaError(cur.detail)
                is VlcParseException -> return if (cur.message == "empty body") ConnectionError.EmptyBody else ConnectionError.Other(cur.message)
                is UnknownHostException -> return ConnectionError.UnknownHost
                is ConnectException -> return if (cur.message?.contains("refused", ignoreCase = true) != false) {
                    ConnectionError.Refused
                } else {
                    ConnectionError.Timeout
                }
                is NoRouteToHostException -> return ConnectionError.Timeout
                is SocketTimeoutException -> return ConnectionError.Timeout
            }
            val name = cur::class.java.simpleName
            val message = cur.message.orEmpty()
            when {
                name.contains("Timeout", ignoreCase = true) -> return ConnectionError.Timeout
                message.contains("refused", ignoreCase = true) -> return ConnectionError.Refused
                message.contains("timed out", ignoreCase = true) -> return ConnectionError.Timeout
                message.contains("unreachable", ignoreCase = true) -> return ConnectionError.Timeout
                message.contains("No address associated", ignoreCase = true) -> return ConnectionError.UnknownHost
            }
            cur = cur.cause
            depth++
        }
        return ConnectionError.Other(t.message ?: t::class.java.simpleName)
    }

    /** Auth errors are not retried automatically — the user has to change the password. */
    fun isAuthError(error: ConnectionError): Boolean =
        error is ConnectionError.WrongPassword || error is ConnectionError.NoPasswordSet
}
