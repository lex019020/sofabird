package dev.vlcremote.app.ui

import android.os.Bundle
import android.view.KeyEvent
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.ui.theme.VlcRemoteTheme
import org.koin.android.ext.android.inject

class MainActivity : ComponentActivity() {

    private val controller: PlayerController by inject()
    private val settings: SettingsRepository by inject()

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        // Test hook (adb): `am start -S -n <package>/dev.vlcremote.app.ui.MainActivity --ez show_permissions true`
        // shows the first-run permissions screen again without clearing the app data.
        val forcePermissions = savedInstanceState == null && intent?.getBooleanExtra(EXTRA_SHOW_PERMISSIONS, false) == true
        setContent {
            val prefs by settings.settings.collectAsStateWithLifecycle()
            VlcRemoteTheme(prefs) {
                AppRoot(forcePermissionsScreen = forcePermissions)
            }
        }
    }

    /**
     * Hardware volume keys drive the PC volume when the setting is on and we are connected;
     * otherwise the system handles them (phone media volume).
     */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val code = event.keyCode
        if (code != KeyEvent.KEYCODE_VOLUME_UP && code != KeyEvent.KEYCODE_VOLUME_DOWN) {
            return super.dispatchKeyEvent(event)
        }
        val prefs = settings.settings.value
        if (!prefs.volumeKeysControlPc || !controller.connection.value.isOnline) {
            return super.dispatchKeyEvent(event)
        }
        if (event.action == KeyEvent.ACTION_DOWN) {
            val delta = if (code == KeyEvent.KEYCODE_VOLUME_UP) prefs.volumeKeyStep else -prefs.volumeKeyStep
            controller.adjustVolume(delta)
        }
        return true // swallow ACTION_UP too, so the system slider never appears
    }

    companion object {
        const val EXTRA_SHOW_PERMISSIONS = "show_permissions"
    }
}
