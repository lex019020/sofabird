package app.sofabird.protocol.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.IOException
import java.io.InputStream

/** Line framing of the RFCOMM byte stream. */
class NdjsonFramingTest {

    private fun utf8(s: String) = s.toByteArray(Charsets.UTF_8)

    @Test
    fun `partial reads are joined into one line`() {
        val framer = NdjsonFramer()
        assertEquals(emptyList<String>(), framer.feed(utf8("""{"t":"pi""")))
        assertEquals(8, framer.pendingBytes)
        assertEquals(emptyList<String>(), framer.feed(utf8("""ng"""")))
        assertEquals(listOf("""{"t":"ping"}"""), framer.feed(utf8("}\n")))
        assertEquals(0, framer.pendingBytes)
    }

    @Test
    fun `several lines in one chunk, the rest stays pending`() {
        val framer = NdjsonFramer()
        val lines = framer.feed(utf8("{\"t\":\"ok\",\"id\":\"1\"}\n{\"t\":\"pong\"}\r\n\n{\"t\":\"st"))
        assertEquals(listOf("""{"t":"ok","id":"1"}""", """{"t":"pong"}""", ""), lines)
        assertEquals(listOf("""{"t":"state"}"""), framer.feed(utf8("ate\"}\n")))
    }

    @Test
    fun `utf8 character split across chunks decodes`() {
        val bytes = utf8("{\"name\":\"Мосфильм ★\"}\n")
        // Split inside every multi-byte sequence in turn.
        for (cut in 1 until bytes.size - 1) {
            val framer = NdjsonFramer()
            val first = framer.feed(bytes, 0, cut)
            val second = framer.feed(bytes, cut, bytes.size - cut)
            assertEquals("cut at $cut", listOf("{\"name\":\"Мосфильм ★\"}"), first + second)
        }
    }

    @Test
    fun `line over the cap is refused`() {
        val framer = NdjsonFramer(maxLineBytes = 16)
        assertEquals(listOf("0123456789abcdef"), framer.feed(utf8("0123456789abcdef\n"))) // exactly the cap
        framer.feed(utf8("0123456789"))
        try {
            framer.feed(utf8("abcdefg"))
            fail("expected LineTooLongException")
        } catch (e: LineTooLongException) {
            assertEquals(16, e.limit)
        }
        assertEquals(0, framer.pendingBytes)
    }

    @Test
    fun `default cap is one mebibyte`() {
        val framer = NdjsonFramer()
        val big = ByteArray(NDJSON_MAX_LINE_BYTES) { 'a'.code.toByte() }
        assertEquals(emptyList<String>(), framer.feed(big))
        assertEquals(NDJSON_MAX_LINE_BYTES, framer.feed(utf8("\n")).single().length)
        try {
            framer.feed(ByteArray(NDJSON_MAX_LINE_BYTES + 1))
            fail("expected LineTooLongException")
        } catch (_: LineTooLongException) {
        }
    }

    /** Hands out at most [step] bytes per read, like a socket. */
    private class ChunkedStream(private val data: ByteArray, private val step: Int) : InputStream() {
        private var pos = 0
        override fun read(): Int = if (pos < data.size) data[pos++].toInt() and 0xff else -1
        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (pos >= data.size) return -1
            val n = minOf(len, step, data.size - pos)
            System.arraycopy(data, pos, b, off, n)
            pos += n
            return n
        }
    }

    @Test
    fun `reader returns lines across small reads and null at eof`() {
        val text = "{\"t\":\"welcome\",\"name\":\"ПК\"}\n\n{\"t\":\"state\"}\n{\"t\":\"unfinished"
        for (step in listOf(1, 2, 3, 7, 64)) {
            val reader = NdjsonReader(ChunkedStream(utf8(text), step), chunkSize = 16)
            assertEquals("""{"t":"welcome","name":"ПК"}""", reader.readLine())
            assertEquals("""{"t":"state"}""", reader.readLine()) // blank line skipped
            assertNull("unfinished last line is dropped at EOF (step $step)", reader.readLine())
            assertNull(reader.readLine())
        }
    }

    @Test
    fun `reader reports eof on an empty stream`() {
        assertNull(NdjsonReader(ByteArrayInputStream(ByteArray(0))).readLine())
    }

    @Test
    fun `io errors propagate to the caller`() {
        val failing = object : InputStream() {
            override fun read(): Int = throw IOException("bt socket closed")
            override fun read(b: ByteArray, off: Int, len: Int): Int = throw IOException("bt socket closed")
        }
        try {
            NdjsonReader(failing).readLine()
            fail("expected IOException")
        } catch (e: IOException) {
            assertTrue(e.message!!.contains("closed"))
        }
    }

    @Test
    fun `decoded lines round-trip through the codec`() {
        val line = CompanionCodec.encodeLine(CompanionMessage.Ping())
        val framer = NdjsonFramer()
        val decoded = CompanionCodec.decode(framer.feed(utf8(line)).single()) as DecodedMessage.Known
        assertTrue(decoded.message is CompanionMessage.Ping)
    }
}
