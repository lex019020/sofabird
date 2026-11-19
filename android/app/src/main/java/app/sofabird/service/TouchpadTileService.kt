package app.sofabird.service

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import app.sofabird.ui.MainActivity

/**
 * The Quick Settings tile "Touchpad": opens the app right on the touchpad and keyboard (the remote stays under it), from
 * anywhere — the pointer of the PC at the TV is one swipe down away. A locked phone is unlocked first.
 */
class TouchpadTileService : TileService() {
    override fun onStartListening() {
        super.onStartListening()
        qsTile?.apply {
            state = Tile.STATE_INACTIVE // an action, not a switch
            updateTile()
        }
    }

    override fun onClick() {
        super.onClick()
        if (isLocked) unlockAndRun(::open) else open()
    }

    private fun open() {
        val intent = MainActivity.touchpadIntent(this).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, flags))
        } else {
            @Suppress("DEPRECATION", "StartActivityAndCollapseDeprecated")
            startActivityAndCollapse(intent)
        }
    }
}
