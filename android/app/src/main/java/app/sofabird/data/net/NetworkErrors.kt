package app.sofabird.data.net

import app.sofabird.protocol.model.ConnectionError
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

/**
 * The user-facing class of a network failure: the first recognisable one in the cause chain (8 levels at most),
 * otherwise [ConnectionError.Other] with the message.
 */
fun classifyNetworkError(t: Throwable): ConnectionError =
    generateSequence(t) { it.cause }.take(MAX_CAUSE_DEPTH).firstNotNullOfOrNull(::classifyOne)
        ?: ConnectionError.Other(t.message ?: t::class.java.simpleName)

private const val MAX_CAUSE_DEPTH = 8

private fun classifyOne(cur: Throwable): ConnectionError? {
    val message = cur.message.orEmpty()
    return when {
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
