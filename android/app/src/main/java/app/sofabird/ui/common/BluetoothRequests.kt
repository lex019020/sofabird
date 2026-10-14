package app.sofabird.ui.common

import android.annotation.SuppressLint
import android.app.Activity
import android.bluetooth.BluetoothAdapter
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.sofabird.R
import app.sofabird.data.companion.BluetoothSupport
import app.sofabird.data.settings.SettingsRepository
import org.koin.mp.KoinPlatform

/**
 * System dialogs the Bluetooth features need; results come back through the callbacks given to [rememberBluetoothRequests].
 * The permission is normally granted on the first-run permissions screen; these in-context requests are the fallback
 * and skip the dialog when it is already granted.
 */
class BluetoothRequests(
    /** Android 12+ "Nearby devices" (`BLUETOOTH_CONNECT`); older Android: reports `true` at once. */
    val requestPermission: () -> Unit,
    /** System "Turn Bluetooth on?" dialog (`ACTION_REQUEST_ENABLE`; needs the permission on Android 12+). */
    val requestEnable: () -> Unit,
    val openAppSettings: () -> Unit,
    val openBluetoothSettings: () -> Unit,
)

@SuppressLint("MissingPermission") // ACTION_REQUEST_ENABLE is only launched after the permission check
@Composable
fun rememberBluetoothRequests(
    onPermissionResult: (Boolean) -> Unit,
    onEnableResult: (Boolean) -> Unit,
): BluetoothRequests {
    val context = LocalContext.current
    val permissionCallback = rememberUpdatedState(onPermissionResult)
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        permissionCallback.value(granted)
    }
    val enableLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        onEnableResult(result.resultCode == Activity.RESULT_OK)
    }
    return remember(context, permissionLauncher, enableLauncher) {
        BluetoothRequests(
            requestPermission = {
                val permission = BluetoothSupport.CONNECT_PERMISSION
                if (permission == null || BluetoothSupport.hasConnectPermission(context)) {
                    permissionCallback.value(true)
                } else {
                    // Lets Settings → Permissions tell "refused for good". Resolved on the tap, not in composition,
                    // so screens using this render without Koin (previews, screenshot tests).
                    KoinPlatform.getKoin().get<SettingsRepository>().noteRequested(listOf(permission))
                    permissionLauncher.launch(permission)
                }
            },
            requestEnable = {
                runCatching { enableLauncher.launch(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE)) }
            },
            openAppSettings = {
                runCatching {
                    context.startActivity(
                        Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null))
                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                    )
                }
            },
            openBluetoothSettings = {
                runCatching { context.startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            },
        )
    }
}

/** In-context rationale shown before the system permission dialog. */
@Composable
fun BluetoothPermissionRationaleDialog(onAllow: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.bt_permission_title)) },
        text = { Text(stringResource(R.string.bt_permission_rationale)) },
        confirmButton = { TextButton(onClick = onAllow) { Text(stringResource(R.string.bt_permission_allow)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.not_now)) } },
    )
}

/** The permission was refused: only the app settings can grant it now. */
@Composable
fun BluetoothPermissionDeniedDialog(onOpenSettings: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.bt_permission_title)) },
        text = { Text(stringResource(R.string.bt_permission_denied)) },
        confirmButton = { TextButton(onClick = onOpenSettings) { Text(stringResource(R.string.bt_open_app_settings)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) } },
    )
}
