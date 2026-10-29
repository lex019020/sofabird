package app.sofabird.ui.common

import android.content.Context
import app.sofabird.R
import app.sofabird.data.companion.BluetoothConnectException
import app.sofabird.data.companion.CompanionHandshakeException
import app.sofabird.data.vlc.VlcErrorClassifier
import app.sofabird.data.vlc.VlcHttpException
import app.sofabird.protocol.companion.CompanionErrorException
import app.sofabird.protocol.companion.ErrorCode
import app.sofabird.protocol.model.ConnectionError

/** Human, action-oriented messages for every error class (see mobile-stack-and-ux §5.5). */
fun ConnectionError.message(context: Context): String = when (this) {
    ConnectionError.NoPasswordSet -> context.getString(R.string.err_no_password_set)
    ConnectionError.WrongPassword -> context.getString(R.string.err_wrong_password)
    ConnectionError.Refused -> context.getString(R.string.err_refused)
    ConnectionError.Timeout -> context.getString(R.string.err_timeout)
    ConnectionError.EmptyBody -> context.getString(R.string.err_empty_body)
    is ConnectionError.LuaError -> context.getString(R.string.err_lua, detail ?: "")
    ConnectionError.UnknownHost -> context.getString(R.string.err_unknown_host)
    ConnectionError.PairingRequired -> context.getString(R.string.err_pairing_required)
    ConnectionError.InvalidPairCode -> context.getString(R.string.err_invalid_pair_code)
    ConnectionError.VlcNotRunning -> context.getString(R.string.err_vlc_not_running)
    is ConnectionError.ProtocolMismatch -> context.getString(R.string.err_protocol_mismatch, remote?.toString() ?: "?")
    ConnectionError.BluetoothFailed -> context.getString(R.string.err_bluetooth_failed)
    ConnectionError.CertificateChanged -> context.getString(R.string.err_certificate_changed)
    ConnectionError.PcAsleep -> context.getString(R.string.err_pc_asleep)
    is ConnectionError.Other -> context.getString(R.string.err_other, message ?: "")
}

/** Short hint shown under the error on the connection screen. */
fun ConnectionError.hint(context: Context): String? = when (this) {
    ConnectionError.NoPasswordSet -> context.getString(R.string.hint_no_password_set)
    ConnectionError.WrongPassword -> context.getString(R.string.hint_wrong_password)
    ConnectionError.Refused -> context.getString(R.string.hint_refused)
    ConnectionError.Timeout -> context.getString(R.string.hint_timeout)
    ConnectionError.EmptyBody -> context.getString(R.string.hint_empty_body)
    ConnectionError.PairingRequired -> context.getString(R.string.hint_pairing_required)
    ConnectionError.InvalidPairCode -> context.getString(R.string.hint_invalid_pair_code)
    ConnectionError.BluetoothFailed -> context.getString(R.string.hint_bluetooth_failed)
    ConnectionError.CertificateChanged -> context.getString(R.string.hint_certificate_changed)
    else -> null
}

fun Throwable.commandErrorMessage(context: Context): String = when (this) {
    is VlcHttpException -> error.message(context)
    is CompanionHandshakeException -> error.message(context)
    is BluetoothConnectException -> ConnectionError.BluetoothFailed.message(context)
    is CompanionErrorException -> when (code) {
        ErrorCode.VLC_NOT_RUNNING -> context.getString(R.string.err_vlc_not_running)
        ErrorCode.UNAUTHORIZED, ErrorCode.PAIRING_REQUIRED -> context.getString(R.string.err_pairing_required)
        ErrorCode.UNSUPPORTED -> context.getString(R.string.err_unsupported)
        else -> context.getString(R.string.err_command_failed, message ?: rawCode)
    }
    is UnsupportedOperationException -> context.getString(R.string.err_needs_companion)
    is IllegalStateException -> context.getString(R.string.err_not_connected)
    else -> VlcErrorClassifier.fromThrowable(this).message(context)
}
