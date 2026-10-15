package app.sofabird.ui

import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import app.sofabird.R
import app.sofabird.data.player.PlayerController
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.ui.theme.SofabirdTheme
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import org.koin.android.ext.android.inject

class MainActivity : ComponentActivity() {

    private val controller: PlayerController by inject()
    private val settings: SettingsRepository by inject()

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        // Test hook (adb): `am start -S -n <package>/app.sofabird.ui.MainActivity --ez show_permissions true`
        // shows the first-run permissions screen again without clearing the app data.
        val forcePermissions = savedInstanceState == null && intent?.getBooleanExtra(EXTRA_SHOW_PERMISSIONS, false) == true
        if (savedInstanceState == null) shareToTv(intent)
        setContent {
            val prefs by settings.settings.collectAsStateWithLifecycle()
            SofabirdTheme(prefs) {
                AppRoot(forcePermissionsScreen = forcePermissions)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        shareToTv(intent)
    }

    /**
     * "Share → Sofabird" (the YouTube app shares "Title https://youtu.be/ID"): the text goes to the companion, which keeps
     * only the video and opens it on the TV. Right after the app starts the PC may not be connected yet, so it waits.
     */
    private fun shareToTv(intent: Intent?) {
        if (intent?.action != Intent.ACTION_SEND) return
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)?.takeIf { it.isNotBlank() } ?: return
        lifecycleScope.launch {
            val online = withTimeoutOrNull(SHARE_WAIT_MS) { controller.connection.first { it.isOnline } }
            if (online == null) {
                Toast.makeText(this@MainActivity, R.string.share_not_connected, Toast.LENGTH_LONG).show()
            } else {
                controller.sendSystem(SystemCommand.YouTubeOpen(text))
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
        private const val SHARE_WAIT_MS = 15_000L
    }
}
