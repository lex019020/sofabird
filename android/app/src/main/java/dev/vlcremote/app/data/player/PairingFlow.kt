package dev.vlcremote.app.data.player

import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.protocol.companion.COMPANION_WS_PORT
import dev.vlcremote.protocol.model.ConnectionError
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlin.coroutines.cancellation.CancellationException

/**
 * A companion to connect to: found on the network, saved earlier, typed in by hand or picked from the
 * phone's bonded Bluetooth devices (then [host] is blank and [btAddress] is set).
 */
data class PairingTarget(
    val host: String,
    val wsPort: Int = COMPANION_WS_PORT,
    val name: String,
    /** Companion installation GUID, when known (discovery / saved server). */
    val pcId: String? = null,
    /** The saved server this target is, when known. */
    val serverId: String? = null,
    /** PC Bluetooth address (bonded device or saved server). */
    val btAddress: String? = null,
) {
    /** No LAN address: the `hello` goes over RFCOMM. */
    val viaBluetooth: Boolean get() = host.isBlank() && btAddress != null
}

/** What the connection screen shows while connecting to a companion. */
sealed interface PairingState {
    data object Idle : PairingState

    /** `hello` in flight. [auto]: started by the app itself (first launch, one PC found). */
    data class Connecting(val target: PairingTarget, val auto: Boolean = false) : PairingState

    /**
     * The phone is not paired; the companion shows a code on the PC screen. [attempt] grows with
     * every rejected code so the UI can clear the field.
     */
    data class EnterCode(
        val target: PairingTarget,
        val submitting: Boolean = false,
        val error: ConnectionError? = null,
        val attempt: Int = 0,
    ) : PairingState

    data class Failed(val target: PairingTarget, val error: ConnectionError) : PairingState

    /** Connected (and paired, when [newlyPaired]); the UI navigates to the remote. */
    data class Done(val server: SavedServer, val newlyPaired: Boolean) : PairingState
}

/** What [PairingFlow] needs from the outside world (fake in tests). */
interface CompanionLink {
    /** Token stored for the saved server this target corresponds to. */
    suspend fun storedToken(target: PairingTarget): String?

    /** One `hello` → `welcome`/`err`, then the socket is closed. */
    suspend fun hello(target: PairingTarget, token: String?, pairCode: String?): TestResult

    /** The stored token was rejected: drop it. */
    suspend fun forgetToken(target: PairingTarget)

    /** Save the server (+ [newToken]) and make it the active connection. */
    suspend fun connect(target: PairingTarget, welcome: TestResult.CompanionOk, newToken: String?): SavedServer
}

/**
 * Connect-or-pair state machine for one companion:
 * `hello` with the stored token → connected; `pairing_required` → the PC shows a code, the user
 * types it → `hello` with `pairCode` → token saved → connected; a wrong code keeps asking.
 */
class PairingFlow(private val scope: CoroutineScope, private val link: CompanionLink) {
    private val _state = MutableStateFlow<PairingState>(PairingState.Idle)
    val state: StateFlow<PairingState> = _state.asStateFlow()

    private var job: Job? = null

    fun start(target: PairingTarget, auto: Boolean = false) {
        job?.cancel()
        _state.value = PairingState.Connecting(target, auto)
        job = scope.launch {
            val token = link.storedToken(target)
            when (val result = link.hello(target, token, pairCode = null)) {
                is TestResult.CompanionOk -> finish(target, result, newToken = null)
                is TestResult.Failed -> when (result.error) {
                    ConnectionError.PairingRequired, ConnectionError.InvalidPairCode -> {
                        if (token != null) link.forgetToken(target)
                        _state.value = PairingState.EnterCode(target)
                    }
                    else -> _state.value = PairingState.Failed(target, result.error)
                }
                is TestResult.DirectOk -> _state.value = PairingState.Failed(target, ConnectionError.Other("not a companion"))
            }
        }
    }

    /** The 6-digit code from the PC screen. Ignored unless a code is expected. */
    fun submitCode(code: String) {
        val current = _state.value as? PairingState.EnterCode ?: return
        if (current.submitting) return
        val digits = code.filter(Char::isDigit)
        if (digits.length != CODE_LENGTH) return
        val target = current.target
        _state.value = current.copy(submitting = true, error = null)
        job = scope.launch {
            when (val result = link.hello(target, token = null, pairCode = digits)) {
                is TestResult.CompanionOk -> {
                    val token = result.token
                    if (token == null) {
                        // The companion accepted the code but issued no token: we could not reconnect later.
                        rejectCode(current, ConnectionError.Other("no token"))
                    } else {
                        finish(target, result, newToken = token)
                    }
                }
                is TestResult.Failed -> rejectCode(current, result.error)
                is TestResult.DirectOk -> rejectCode(current, ConnectionError.Other("not a companion"))
            }
        }
    }

    private fun rejectCode(previous: PairingState.EnterCode, error: ConnectionError) {
        // pairing_required in answer to a code = the code expired and a new one is on the screen.
        val shown = if (error == ConnectionError.PairingRequired) ConnectionError.InvalidPairCode else error
        _state.value = previous.copy(submitting = false, error = shown, attempt = previous.attempt + 1)
    }

    private suspend fun finish(target: PairingTarget, welcome: TestResult.CompanionOk, newToken: String?) {
        val server = try {
            link.connect(target, welcome, newToken)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            _state.value = PairingState.Failed(target, ConnectionError.Other(e.message))
            return
        }
        _state.value = PairingState.Done(server, newlyPaired = newToken != null)
    }

    /** Try again after [PairingState.Failed]. */
    fun retry() {
        val target = when (val s = _state.value) {
            is PairingState.Failed -> s.target
            else -> return
        }
        start(target)
    }

    fun cancel() {
        job?.cancel()
        job = null
        _state.value = PairingState.Idle
    }

    /** The UI has navigated away after [PairingState.Done]. */
    fun consumeDone() {
        if (_state.value is PairingState.Done) _state.value = PairingState.Idle
    }

    companion object {
        const val CODE_LENGTH = 6
    }
}
