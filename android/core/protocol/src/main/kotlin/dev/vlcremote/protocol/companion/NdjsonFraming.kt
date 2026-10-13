package dev.vlcremote.protocol.companion

import java.io.IOException
import java.io.InputStream

/** The peer sent a line longer than the protocol allows: the stream is out of sync, drop the link. */
class LineTooLongException(val limit: Int) : IOException("NDJSON line exceeds $limit bytes")

/**
 * Splits a byte stream into `\n`-terminated UTF-8 lines (NDJSON over RFCOMM). Bytes are buffered
 * until the separator, so a multi-byte character split across reads decodes correctly. A trailing
 * `\r` is dropped. Lines longer than [maxLineBytes] throw [LineTooLongException].
 */
class NdjsonFramer(private val maxLineBytes: Int = NDJSON_MAX_LINE_BYTES) {
    private var buffer = ByteArray(INITIAL_CAPACITY)
    private var size = 0

    /** Bytes of an unfinished line waiting for its `\n`. */
    val pendingBytes: Int get() = size

    /** Feeds a chunk and returns every line it completed (possibly none, possibly blank ones). */
    fun feed(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size - offset): List<String> {
        require(offset >= 0 && length >= 0 && offset + length <= bytes.size) { "bad range" }
        var lines: MutableList<String>? = null
        var start = offset
        val end = offset + length
        for (i in offset until end) {
            if (bytes[i] == NEWLINE) {
                append(bytes, start, i - start)
                (lines ?: ArrayList<String>(2).also { lines = it }).add(takeLine())
                start = i + 1
            }
        }
        append(bytes, start, end - start)
        return lines ?: emptyList()
    }

    /** Forgets an unfinished line (end of stream: an incomplete message is never delivered). */
    fun reset() {
        size = 0
        shrink()
    }

    private fun append(bytes: ByteArray, from: Int, count: Int) {
        if (count == 0) return
        if (size + count > maxLineBytes) {
            reset()
            throw LineTooLongException(maxLineBytes)
        }
        if (size + count > buffer.size) {
            var capacity = buffer.size
            while (capacity < size + count) capacity *= 2
            buffer = buffer.copyOf(capacity.coerceAtMost(maxLineBytes))
        }
        System.arraycopy(bytes, from, buffer, size, count)
        size += count
    }

    private fun takeLine(): String {
        var n = size
        if (n > 0 && buffer[n - 1] == CR) n--
        val line = String(buffer, 0, n, Charsets.UTF_8)
        size = 0
        shrink()
        return line
    }

    /** Do not keep a megabyte around after one big message. */
    private fun shrink() {
        if (buffer.size > SHRINK_ABOVE) buffer = ByteArray(INITIAL_CAPACITY)
    }

    private companion object {
        const val NEWLINE = '\n'.code.toByte()
        const val CR = '\r'.code.toByte()
        const val INITIAL_CAPACITY = 4 * 1024
        const val SHRINK_ABOVE = 64 * 1024
    }
}

/**
 * Blocking NDJSON reader over an [InputStream] (a Bluetooth socket). [readLine] returns the next
 * non-blank line, or `null` at end of stream: `read() == -1` (Android 17 reports a closed RFCOMM
 * link this way) — an unfinished last line is discarded. I/O errors propagate as [IOException];
 * callers treat both as a disconnect.
 */
class NdjsonReader(
    private val input: InputStream,
    maxLineBytes: Int = NDJSON_MAX_LINE_BYTES,
    chunkSize: Int = 8 * 1024,
) {
    private val framer = NdjsonFramer(maxLineBytes)
    private val ready = ArrayDeque<String>()
    private val chunk = ByteArray(chunkSize)
    private var eof = false

    fun readLine(): String? {
        while (true) {
            while (ready.isNotEmpty()) {
                val line = ready.removeFirst()
                if (line.isNotBlank()) return line
            }
            if (eof) return null
            val n = input.read(chunk)
            if (n < 0) {
                eof = true
                framer.reset()
                continue
            }
            if (n > 0) ready.addAll(framer.feed(chunk, 0, n))
        }
    }
}
