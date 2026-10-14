package app.sofabird.data.player

import android.content.Context
import android.os.Build
import android.provider.Settings
import app.sofabird.data.secrets.SecretStore
import app.sofabird.protocol.companion.DeviceInfo

/**
 * What this phone says about itself in `hello.device`. The companion shows the name in the pairing
 * window on the PC ("Galaxy S24 wants to connect"), so prefer the user-visible device name
 * (Settings → About phone) over the model code.
 */
class DeviceIdentity(context: Context, private val secrets: SecretStore) {
    private val appContext = context.applicationContext

    fun deviceInfo(): DeviceInfo =
        DeviceInfo(name = name(), id = secrets.deviceId(), locale = java.util.Locale.getDefault().toLanguageTag())

    fun name(): String {
        val userName = runCatching {
            Settings.Global.getString(appContext.contentResolver, Settings.Global.DEVICE_NAME)
        }.getOrNull()
        if (!userName.isNullOrBlank()) return userName.trim()
        return listOfNotNull(Build.MANUFACTURER, Build.MODEL)
            .joinToString(" ")
            .ifBlank { "Android" }
            .replaceFirstChar { it.uppercase() }
    }
}
