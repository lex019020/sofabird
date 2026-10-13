package dev.vlcremote.app.data.player

import android.content.Context
import android.os.Build
import android.provider.Settings
import dev.vlcremote.app.data.secrets.SecretStore
import dev.vlcremote.protocol.companion.DeviceInfo

/**
 * What this phone says about itself in `hello.device`. The companion shows the name in the pairing
 * window on the PC ("Galaxy S24 wants to connect"), so prefer the user-visible device name
 * (Settings → About phone) over the model code.
 */
class DeviceIdentity(context: Context, private val secrets: SecretStore) {
    private val appContext = context.applicationContext

    fun deviceInfo(): DeviceInfo = DeviceInfo(name = name(), id = secrets.deviceId())

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
