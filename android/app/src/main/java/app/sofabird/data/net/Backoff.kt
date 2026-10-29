package app.sofabird.data.net

import kotlin.random.Random

/**
 * Delay before the next reconnect attempt: 1 s, 2 s, 4 s … up to [capMs], plus a random jitter of up to [jitterMs]
 * so that several phones (or a phone and its own retries) do not knock on the PC in step.
 */
class Backoff(private val maxDoublings: Int, private val capMs: Long, private val jitterMs: Long) {

    fun delayMillis(attempt: Int, random: Random = Random): Long {
        val base = (FIRST_DELAY_MS shl attempt.coerceIn(0, maxDoublings)).coerceAtMost(capMs)
        return base + random.nextLong(0, jitterMs)
    }

    companion object {
        private const val FIRST_DELAY_MS = 1_000L

        /** The companion link: up to 15 s between attempts, so a PC woken by hand is found soon. */
        val CompanionLink = Backoff(maxDoublings = 4, capMs = 15_000, jitterMs = 750)
    }
}
