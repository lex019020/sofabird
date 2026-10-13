package dev.vlcremote.app.ui.permissions

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.domain.permissions.PermissionItem
import dev.vlcremote.app.domain.permissions.PermissionRegistry
import dev.vlcremote.app.domain.permissions.PermissionStatus
import dev.vlcremote.app.domain.permissions.permissionStatus
import org.koin.compose.koinInject

/** The registry items that apply to this phone, with live statuses (re-read on resume and after each request). */
@Stable
class AppPermissionsState(
    val items: List<PermissionItem>,
    val statuses: Map<PermissionItem, PermissionStatus>,
    private val launch: (List<PermissionItem>) -> Unit,
    val openAppSettings: () -> Unit,
) {
    fun status(item: PermissionItem): PermissionStatus = statuses[item] ?: PermissionStatus.NOT_REQUESTED

    val allGranted: Boolean get() = items.all { status(it) == PermissionStatus.GRANTED }

    /** Items a system dialog can still be shown for. */
    val requestable: List<PermissionItem> get() = items.filter { status(it).canRequest }

    /** One system request for all requestable items (no-op when there are none). */
    fun requestAll() = request(requestable)

    fun request(items: List<PermissionItem>) {
        if (items.isNotEmpty()) launch(items)
    }
}

/**
 * Shared by the first-run screen and the settings section. [onResult] gets the statuses after a request.
 * "Nearby devices" granted → the companion backend retries at once (a pending Bluetooth fallback).
 */
@Composable
fun rememberAppPermissions(onResult: (AppPermissionsState) -> Unit = {}): AppPermissionsState {
    val context = LocalContext.current
    val settings: SettingsRepository = koinInject()
    val controller: PlayerController = koinInject()
    val requested by settings.requestedPermissions.collectAsStateWithLifecycle()
    var tick by remember { mutableIntStateOf(0) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { tick++ }

    val items = remember { PermissionRegistry.applicable(Build.VERSION.SDK_INT) }
    var pendingResult by remember { mutableIntStateOf(0) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        if (result[Manifest.permission.BLUETOOTH_CONNECT] == true) controller.retryNow()
        tick++
        pendingResult++
    }

    val statuses = remember(tick, requested) {
        val activity = context.findActivity()
        items.associateWith { item ->
            permissionStatus(
                granted = isGranted(context, item.permission),
                shouldShowRationale = activity?.let { ActivityCompat.shouldShowRequestPermissionRationale(it, item.permission) } == true,
                requestedBefore = item.permission in requested,
            )
        }
    }
    val state = AppPermissionsState(
        items = items,
        statuses = statuses,
        launch = { toRequest ->
            val permissions = toRequest.map { it.permission }
            settings.noteRequested(permissions)
            runCatching { launcher.launch(permissions.toTypedArray()) }
        },
        openAppSettings = { openAppDetails(context) },
    )

    // Deliver the result once the statuses reflect it (the same composition bumped `tick`).
    val callback = rememberUpdatedState(onResult)
    val latest = rememberUpdatedState(state)
    LaunchedEffect(pendingResult) {
        if (pendingResult > 0) callback.value(latest.value)
    }
    return state
}

fun isGranted(context: Context, permission: String): Boolean =
    ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

fun openAppDetails(context: Context) {
    runCatching {
        context.startActivity(
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}
