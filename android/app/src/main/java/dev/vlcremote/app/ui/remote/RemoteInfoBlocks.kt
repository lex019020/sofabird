package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import dev.vlcremote.app.R
import dev.vlcremote.app.data.companion.BluetoothStatus
import dev.vlcremote.app.ui.common.BluetoothPermissionDeniedDialog
import dev.vlcremote.app.ui.common.rememberBluetoothRequests
import dev.vlcremote.app.ui.common.withNameBreaks
import dev.vlcremote.protocol.model.ConnectionState

@Composable
internal fun Artwork(ui: RemoteUiState, modifier: Modifier) {
    Box(
        modifier
            .aspectRatio(16f / 9f)
            .clip(RoundedCornerShape(16.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        val art = ui.state.item.artUrl
        if (art != null) {
            AsyncImage(
                model = art,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Icon(
                Icons.Default.Movie,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                modifier = Modifier.fillMaxSize(0.3f),
            )
        }
    }
}

@Composable
internal fun TitleBlock(ui: RemoteUiState) {
    val title = ui.state.item.title ?: when {
        !ui.connection.isOnline -> stringResource(R.string.not_connected_title)
        !ui.state.vlc.running && ui.isCompanion -> stringResource(R.string.vlc_not_running_title)
        else -> stringResource(R.string.nothing_playing)
    }
    Text(
        title.withNameBreaks(),
        style = MaterialTheme.typography.titleLarge,
        maxLines = 2,
        overflow = TextOverflow.Ellipsis,
        textAlign = TextAlign.Center,
        modifier = Modifier.fillMaxWidth(),
    )
    val ch = ui.state.chapters
    if (ch.count > 0 && ch.index >= 0) {
        val name = ch.names?.getOrNull(ch.index)
        Text(
            if (name.isNullOrBlank()) stringResource(R.string.chapter_n_of, ch.index + 1, ch.count)
            else stringResource(R.string.chapter_n_named, ch.index + 1, ch.count, name),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
internal fun OfflineCard(ui: RemoteUiState, onOpenConnection: () -> Unit) {
    if (ui.connection is ConnectionState.Failed || ui.connection is ConnectionState.Disconnected) {
        Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
            Column(Modifier.padding(16.dp)) {
                Text(
                    ui.hostName?.let { stringResource(R.string.no_link_with, it) } ?: stringResource(R.string.no_server_configured),
                    style = MaterialTheme.typography.titleMedium,
                )
                Spacer(Modifier.height(8.dp))
                Button(onClick = onOpenConnection) { Text(stringResource(R.string.action_connect)) }
            }
        }
    }
}

/**
 * The LAN is down and the PC could be reached over Bluetooth once the user grants "Nearby devices"
 * or turns Bluetooth on. The card is the in-context rationale; "Allow" opens the system dialog.
 */
@Composable
internal fun BluetoothFallbackCard(ui: RemoteUiState, onReady: () -> Unit, onDismiss: () -> Unit) {
    val need = ui.bluetoothNeed ?: return
    if (ui.connection.isOnline) return
    var denied by remember { mutableStateOf(false) }
    val requests = rememberBluetoothRequests(
        onPermissionResult = { granted -> if (granted) onReady() else denied = true },
        onEnableResult = { on -> if (on) onReady() },
    )
    Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Bluetooth, null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(8.dp))
                Text(
                    stringResource(R.string.bt_fallback_title, ui.hostName.orEmpty()),
                    style = MaterialTheme.typography.titleMedium,
                )
            }
            Spacer(Modifier.height(6.dp))
            Text(
                stringResource(
                    if (need == BluetoothStatus.OFF) R.string.bt_fallback_off_body else R.string.bt_fallback_permission_body,
                ),
                style = MaterialTheme.typography.bodyMedium,
            )
            Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = onDismiss) { Text(stringResource(R.string.not_now)) }
                Spacer(Modifier.width(8.dp))
                if (need == BluetoothStatus.OFF) {
                    Button(onClick = requests.requestEnable) { Text(stringResource(R.string.bt_turn_on)) }
                } else {
                    Button(onClick = requests.requestPermission) { Text(stringResource(R.string.bt_permission_allow)) }
                }
            }
        }
    }
    if (denied) {
        BluetoothPermissionDeniedDialog(
            onOpenSettings = {
                denied = false
                requests.openAppSettings()
            },
            onDismiss = { denied = false },
        )
    }
}
