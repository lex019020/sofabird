package app.sofabird.ui

import android.content.Context
import android.util.Log
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import app.sofabird.R

/**
 * The launcher shortcut "Touchpad" (a long press on the app's icon): a dynamic one, so that it targets this very app —
 * the debug build has another package name, which a static `shortcuts.xml` would have to spell out.
 */
object TouchpadShortcut {
    private const val ID = "touchpad"

    /** Called when the app opens (in front: publishing from the background is rate-limited). */
    fun publish(context: Context) {
        runCatching {
            val shortcut = ShortcutInfoCompat.Builder(context, ID)
                .setShortLabel(context.getString(R.string.tile_touchpad))
                .setLongLabel(context.getString(R.string.shortcut_touchpad_long))
                .setIcon(IconCompat.createWithResource(context, R.drawable.ic_shortcut_touchpad))
                .setIntent(MainActivity.touchpadIntent(context))
                .build()
            ShortcutManagerCompat.pushDynamicShortcut(context, shortcut)
        }.onFailure { Log.d("TouchpadShortcut", "shortcut not published: ${it.message}") }
    }
}
