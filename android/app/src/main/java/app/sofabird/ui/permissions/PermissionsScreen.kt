package app.sofabird.ui.permissions

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Block
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.VerifiedUser
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.domain.permissions.PermissionItem
import app.sofabird.domain.permissions.PermissionKind
import app.sofabird.domain.permissions.PermissionRegistry
import app.sofabird.domain.permissions.PermissionStatus

/**
 * First-run "Permissions" screen: what the app will ask for and why, before any system dialog.
 * "Continue" asks for everything at once; all granted → [onDone] at once, otherwise the cards show
 * the result (and "Open settings" for permanent refusals) and the next "Continue" goes on.
 * "Not now" and Back skip; the in-context requests remain as a fallback.
 */
@Composable
fun PermissionsScreen(onDone: () -> Unit) {
    var asked by rememberSaveable { mutableStateOf(false) }
    val permissions = rememberAppPermissions(onResult = { state -> if (state.allGranted) onDone() })
    BackHandler(onBack = onDone)
    PermissionsContent(
        items = permissions.items,
        status = permissions::status,
        asked = asked,
        onContinue = {
            if (!asked && permissions.requestable.isNotEmpty()) {
                asked = true
                permissions.requestAll()
            } else {
                onDone()
            }
        },
        onNotNow = onDone,
        onRequest = { permissions.request(listOf(it)) },
        onOpenSettings = permissions.openAppSettings,
    )
}

@Composable
private fun PermissionsContent(
    items: List<PermissionItem>,
    status: (PermissionItem) -> PermissionStatus,
    asked: Boolean,
    onContinue: () -> Unit,
    onNotNow: () -> Unit,
    onRequest: (PermissionItem) -> Unit,
    onOpenSettings: () -> Unit,
) {
    Scaffold(
        bottomBar = {
            Surface(tonalElevation = 3.dp) {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .windowInsetsPadding(WindowInsets.navigationBars)
                        .padding(horizontal = 16.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    // Stacked full-width buttons: long translations and large fonts never squeeze them.
                    Button(onClick = onContinue, modifier = Modifier.fillMaxWidth()) {
                        Text(stringResource(R.string.perm_continue), textAlign = TextAlign.Center)
                    }
                    if (!asked) {
                        TextButton(onClick = onNotNow, modifier = Modifier.fillMaxWidth()) {
                            Text(stringResource(R.string.not_now), textAlign = TextAlign.Center)
                        }
                    }
                }
            }
        },
    ) { padding ->
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Icon(
                Icons.Outlined.VerifiedUser,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(40.dp),
            )
            Text(stringResource(R.string.perm_screen_title), style = MaterialTheme.typography.headlineSmall)
            Text(
                stringResource(R.string.perm_screen_intro),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            items.forEach { item ->
                PermissionCard(
                    item = item,
                    status = status(item),
                    showNotAskedStatus = false,
                    showRequestButton = asked,
                    onRequest = { onRequest(item) },
                    onOpenSettings = onOpenSettings,
                )
            }
        }
    }
}

/**
 * One permission: icon, name, status, why it is needed and what does not work without it.
 * [showRequestButton] adds "Allow" while the system dialog can still be shown; a permanent refusal
 * always gets "Open settings" (the app details page).
 */
@Composable
fun PermissionCard(
    item: PermissionItem,
    status: PermissionStatus,
    showNotAskedStatus: Boolean,
    showRequestButton: Boolean,
    onRequest: () -> Unit,
    onOpenSettings: () -> Unit,
    modifier: Modifier = Modifier,
) {
    OutlinedCard(modifier.fillMaxWidth()) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.Top) {
            Icon(
                item.kind.icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(28.dp),
            )
            Spacer(Modifier.width(14.dp))
            // Texts wrap freely (no maxLines): the column takes the remaining width.
            Column(Modifier.weight(1f)) {
                Text(stringResource(item.title), style = MaterialTheme.typography.titleMedium)
                if (status != PermissionStatus.NOT_REQUESTED || showNotAskedStatus) {
                    Spacer(Modifier.height(2.dp))
                    StatusLine(status)
                }
                Spacer(Modifier.height(6.dp))
                Text(stringResource(item.rationale), style = MaterialTheme.typography.bodyMedium)
                Spacer(Modifier.height(4.dp))
                Text(
                    stringResource(item.without),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                when {
                    status == PermissionStatus.PERMANENTLY_DENIED -> OutlinedButton(
                        onClick = onOpenSettings,
                        modifier = Modifier.padding(top = 10.dp),
                        contentPadding = ButtonDefaults.ButtonWithIconContentPadding,
                    ) {
                        Icon(Icons.AutoMirrored.Filled.OpenInNew, null, Modifier.size(18.dp))
                        Spacer(Modifier.width(8.dp))
                        Text(stringResource(R.string.perm_open_settings))
                    }
                    showRequestButton && status.canRequest -> FilledTonalButton(
                        onClick = onRequest,
                        modifier = Modifier.padding(top = 10.dp),
                    ) { Text(stringResource(R.string.perm_allow)) }
                }
            }
        }
    }
}

@Composable
private fun StatusLine(status: PermissionStatus) {
    val (icon, color, text) = when (status) {
        PermissionStatus.GRANTED -> Triple(Icons.Default.CheckCircle, GrantedGreen, R.string.perm_status_granted)
        PermissionStatus.PERMANENTLY_DENIED -> Triple(Icons.Default.Block, MaterialTheme.colorScheme.error, R.string.perm_status_blocked)
        PermissionStatus.DENIED, PermissionStatus.NOT_REQUESTED ->
            Triple(Icons.Outlined.Info, MaterialTheme.colorScheme.onSurfaceVariant, R.string.perm_status_not_granted)
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(16.dp))
        Spacer(Modifier.width(6.dp))
        Text(stringResource(text), style = MaterialTheme.typography.labelLarge, color = color)
    }
}

private val GrantedGreen = Color(0xFF4CAF50)

val PermissionKind.icon: ImageVector
    get() = when (this) {
        PermissionKind.NOTIFICATIONS -> Icons.Default.Notifications
        PermissionKind.NEARBY_DEVICES -> Icons.Default.Bluetooth
    }

// ---------------------------------------------------------------------------- previews
// Long Russian texts and font scale 1.3: nothing may be cut off or squeezed.

@Preview(name = "en", widthDp = 360, heightDp = 900)
@Preview(name = "ru, font 1.3", locale = "ru", fontScale = 1.3f, widthDp = 360, heightDp = 1100)
@Composable
private fun PermissionsScreenPreview() {
    MaterialTheme(colorScheme = darkColorScheme()) {
        PermissionsContent(
            items = PermissionRegistry.all,
            status = { PermissionStatus.NOT_REQUESTED },
            asked = false,
            onContinue = {},
            onNotNow = {},
            onRequest = {},
            onOpenSettings = {},
        )
    }
}

@Preview(name = "after request, ru, font 1.3", locale = "ru", fontScale = 1.3f, widthDp = 320, heightDp = 1100)
@Composable
private fun PermissionsAfterRequestPreview() {
    MaterialTheme(colorScheme = darkColorScheme()) {
        PermissionsContent(
            items = PermissionRegistry.all,
            status = { if (it.kind == PermissionKind.NOTIFICATIONS) PermissionStatus.GRANTED else PermissionStatus.PERMANENTLY_DENIED },
            asked = true,
            onContinue = {},
            onNotNow = {},
            onRequest = {},
            onOpenSettings = {},
        )
    }
}
