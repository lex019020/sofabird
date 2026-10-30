package app.sofabird.ui.touchpad

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.util.Log
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.platform.LocalContext

/**
 * While the touchpad is on the screen, Wi-Fi stays out of its power saving: in it the phone's radio wakes up only every
 * ~100 ms to send and receive, which a pointer feels as lag and jerks. Android keeps the low-latency mode only while the
 * app is in front with the screen on, so it ends by itself too.
 */
@Composable
internal fun LowLatencyWifi(active: Boolean) {
    val context = LocalContext.current
    DisposableEffect(active) {
        val lock = if (active) acquire(context) else null
        onDispose { lock?.let(::release) }
    }
}

private fun acquire(context: Context): WifiManager.WifiLock? = runCatching {
    val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        WifiManager.WIFI_MODE_FULL_LOW_LATENCY
    } else {
        @Suppress("DEPRECATION")
        WifiManager.WIFI_MODE_FULL_HIGH_PERF
    }
    wifi.createWifiLock(mode, TAG).apply {
        setReferenceCounted(false)
        acquire()
    }
}.onFailure { Log.d(TAG, "no low-latency Wi-Fi: ${it.message}") }.getOrNull()

private fun release(lock: WifiManager.WifiLock) {
    runCatching { if (lock.isHeld) lock.release() }
}

private const val TAG = "SofabirdTouchpad"
