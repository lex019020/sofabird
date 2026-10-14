package app.sofabird.ui.permissions

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/**
 * Settings → "Permissions": the same registry and cards as the first-run screen, with the current
 * status and "Allow" / "Open settings". Nothing to show on Android versions without runtime permissions.
 */
@Composable
fun PermissionsSettingsSection(modifier: Modifier = Modifier, header: @Composable () -> Unit) {
    val permissions = rememberAppPermissions()
    if (permissions.items.isEmpty()) return
    header()
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        permissions.items.forEach { item ->
            PermissionCard(
                item = item,
                status = permissions.status(item),
                showNotAskedStatus = true,
                showRequestButton = true,
                onRequest = { permissions.request(listOf(item)) },
                onOpenSettings = permissions.openAppSettings,
            )
        }
    }
}
