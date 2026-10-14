package app.sofabird.data.companion

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import app.sofabird.protocol.companion.COMPANION_RFCOMM_UUID
import app.sofabird.protocol.companion.NdjsonReader
import app.sofabird.protocol.model.TransportKind
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.cancellation.CancellationException

/**
 * Bluetooth Classic RFCOMM link to the companion service (secure socket: the PC must be bonded,
 * the channel is encrypted). NDJSON: one UTF-8 JSON object per `\n`-terminated line, ≤ 1 MiB.
 * `read() == -1` (Android 17) and [IOException] both end the session.
 */
@SuppressLint("MissingPermission") // callers check BLUETOOTH_CONNECT (BluetoothSupport.status)
class RfcommTransport private constructor(private val socket: BluetoothSocket) : CompanionTransport {
    override val kind: TransportKind = TransportKind.BLUETOOTH

    private val reader = NdjsonReader(socket.inputStream)
    private val output = socket.outputStream
    private val writeLock = Mutex()

    @Volatile private var closed = false

    override suspend fun send(text: String) {
        val bytes = (text.trimEnd('\n', '\r') + "\n").toByteArray(Charsets.UTF_8)
        writeLock.withLock {
            withContext(Dispatchers.IO) {
                output.write(bytes)
                output.flush()
            }
        }
    }

    override suspend fun receive(): String? {
        if (closed) return null
        return try {
            blockingCancellable(abort = ::closeSocket) { reader.readLine() }
        } catch (e: IOException) {
            if (closed) null else throw e
        }
    }

    override suspend fun close() = closeSocket()

    private fun closeSocket() {
        closed = true
        runCatching { socket.close() }
    }

    companion object {
        val SERVICE_UUID: UUID = UUID.fromString(COMPANION_RFCOMM_UUID)

        /** SDP lookup + connect usually takes 1–3 s; a missing PC fails within the stack's own ~12 s. */
        const val CONNECT_TIMEOUT_MS = 12_000L

        /**
         * Connects to the companion service on [device]. [cancelDiscovery]: whether the app may call
         * `cancelDiscovery()` (Android 12+ needs `BLUETOOTH_SCAN` for it, which we do not ask for —
         * we never start discovery ourselves). Throws [BluetoothConnectException].
         */
        suspend fun connect(
            adapter: BluetoothAdapter,
            device: BluetoothDevice,
            cancelDiscovery: Boolean,
            timeoutMs: Long = CONNECT_TIMEOUT_MS,
        ): RfcommTransport {
            val socket = try {
                withContext(Dispatchers.IO) { device.createRfcommSocketToServiceRecord(SERVICE_UUID) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) { // IOException, SecurityException (permission revoked)
                throw BluetoothConnectException("cannot create RFCOMM socket: ${e.message}", e)
            }
            try {
                if (cancelDiscovery) runCatching { adapter.cancelDiscovery() } // discovery slows connect() down
                val connected = withTimeoutOrNull(timeoutMs) {
                    blockingCancellable(abort = { runCatching { socket.close() } }) { socket.connect() }
                    true
                }
                if (connected == null) throw BluetoothConnectException("RFCOMM connect timed out")
                return RfcommTransport(socket)
            } catch (e: CancellationException) {
                runCatching { socket.close() }
                throw e
            } catch (e: BluetoothConnectException) {
                runCatching { socket.close() }
                throw e
            } catch (e: Exception) {
                runCatching { socket.close() }
                throw BluetoothConnectException("RFCOMM connect failed: ${e.message}", e)
            }
        }
    }
}

/**
 * Runs a blocking call on the IO pool. If the caller is cancelled meanwhile, [abort] is invoked
 * (closing the socket) so the call returns instead of pinning the coroutine forever — blocking
 * Bluetooth socket calls ignore thread interrupts.
 */
internal suspend fun <T> blockingCancellable(abort: () -> Unit, block: () -> T): T = coroutineScope {
    val done = AtomicBoolean(false)
    val watcher = launch(start = CoroutineStart.UNDISPATCHED) {
        try {
            awaitCancellation()
        } finally {
            if (!done.get()) abort()
        }
    }
    try {
        runInterruptible(Dispatchers.IO) { block() }
    } finally {
        done.set(true)
        watcher.cancel()
    }
}
