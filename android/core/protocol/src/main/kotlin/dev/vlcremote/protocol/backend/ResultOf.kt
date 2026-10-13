package dev.vlcremote.protocol.backend

import kotlinx.coroutines.CancellationException

/**
 * [runCatching] for suspend code: failures become a [Result], but cancellation is rethrown, so a cancelled caller
 * stops instead of handling a "failure" (a reload that replaced an earlier load, a disconnect).
 */
@Suppress("TooGenericExceptionCaught") // turning every failure into a Result is the point
inline fun <T> resultOf(block: () -> T): Result<T> = try {
    Result.success(block())
} catch (e: CancellationException) {
    throw e
} catch (e: Throwable) {
    Result.failure(e)
}
