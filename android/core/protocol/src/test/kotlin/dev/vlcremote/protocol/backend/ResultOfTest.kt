package dev.vlcremote.protocol.backend

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ResultOfTest {

    @Test
    fun `failures become a Result`() {
        val result = resultOf { error("boom") }
        assertTrue(result.isFailure)
        assertEquals("boom", result.exceptionOrNull()?.message)
        assertEquals(42, resultOf { 42 }.getOrNull())
    }

    @Test(expected = CancellationException::class)
    fun `cancellation is rethrown`() {
        resultOf { throw CancellationException("disconnected") }
    }

    @Test
    fun `a cancelled caller stops instead of handling a failure`() = runTest {
        var handled = false
        val job = async {
            val result = resultOf { awaitCancellation() }
            handled = true // never reached: the cancellation propagates
            result
        }
        yield()
        job.cancel()
        assertTrue(runCatching { job.await() }.exceptionOrNull() is CancellationException)
        assertFalse(handled)
    }
}
