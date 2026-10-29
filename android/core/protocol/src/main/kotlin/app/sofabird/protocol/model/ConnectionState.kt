package app.sofabird.protocol.model

/** Why a connection attempt failed — mapped to distinct, human messages in the UI. */
sealed class ConnectionError {
    /** HTTP 403: VLC refuses because no `http-password` is configured. */
    data object NoPasswordSet : ConnectionError()

    /** HTTP 401: password mismatch. */
    data object WrongPassword : ConnectionError()

    /** TCP connection refused: web interface disabled or wrong port. */
    data object Refused : ConnectionError()

    /** No answer in time: firewall, other network, PC asleep. */
    data object Timeout : ConnectionError()

    /** 200 with an empty body: VLC is still starting. */
    data object EmptyBody : ConnectionError()

    /** 200 with HTML `<title>Error loading` — a Lua page failed. */
    data class LuaError(val detail: String? = null) : ConnectionError()

    /** Host name could not be resolved. */
    data object UnknownHost : ConnectionError()

    /**
     * Companion: this phone is not paired (no token or the token was revoked). A current companion
     * already shows a code on the PC screen; the app asks for it.
     */
    data object PairingRequired : ConnectionError()

    /** Companion: the pairing code is wrong or expired (or too many attempts). */
    data object InvalidPairCode : ConnectionError()

    /** Companion reports VLC is not running (level 2 only; UI offers "launch"). */
    data object VlcNotRunning : ConnectionError()

    /** Companion speaks an incompatible protocol version. */
    data class ProtocolMismatch(val remote: Int?) : ConnectionError()

    /** Companion over Bluetooth: the RFCOMM connection failed (PC not bonded, out of range, companion not running). */
    data object BluetoothFailed : ConnectionError()

    /**
     * Companion: the PC presented another certificate than the one pinned when this phone paired (the companion was
     * reinstalled with its data removed — or something pretends to be the PC). Not retried.
     */
    data object CertificateChanged : ConnectionError()

    /**
     * Companion: the PC said it was going to sleep (`event sleeping`) and has not answered since. Retried with a
     * backoff (it may wake by itself); the remote offers to wake it.
     */
    data object PcAsleep : ConnectionError()

    data class Other(val message: String?) : ConnectionError()
}

/** Which link carries the companion session. Direct VLC is always [WIFI]. */
enum class TransportKind { WIFI, BLUETOOTH }

/** Connection lifecycle of a backend. Never blocks the UI: degraded states are shown inline. */
sealed class ConnectionState {
    data object Disconnected : ConnectionState()

    /** Connecting or quietly re-connecting; [lastError] explains why the previous attempt failed. */
    data class Connecting(val attempt: Int = 0, val lastError: ConnectionError? = null) : ConnectionState()

    data class Connected(
        val hostName: String,
        /** 0/1 — direct VLC, 2 — companion. */
        val level: Int,
        val latencyMs: Long? = null,
        val transport: TransportKind = TransportKind.WIFI,
    ) : ConnectionState()

    /** Connected before, now several polls failed in a row; commands are buffered briefly. */
    data class Degraded(
        val hostName: String,
        val failures: Int,
        val transport: TransportKind = TransportKind.WIFI,
    ) : ConnectionState()

    data class Failed(val error: ConnectionError) : ConnectionState()

    /** Link of an online session (`null` when not online). */
    val transportOrNull: TransportKind?
        get() = when (this) {
            is Connected -> transport
            is Degraded -> transport
            else -> null
        }

    val isConnected: Boolean get() = this is Connected
    val isOnline: Boolean get() = this is Connected || this is Degraded
}
