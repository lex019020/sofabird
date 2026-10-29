package app.sofabird.ui.remote

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.data.companion.BluetoothStatus
import app.sofabird.data.player.PcWaker
import app.sofabird.ui.common.BluetoothPermissionDeniedDialog
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.rememberBluetoothRequests
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.withNameBreaks
import coil3.compose.AsyncImage

/** The cover of what plays, with "Stop" in its corner (away from Play, so it is not hit by accident). */
@Composable
internal fun Artwork(ui: RemoteUiState, modifier: Modifier, onStop: () -> Unit = {}) {
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
        if (ui.connection.isOnline && !ui.state.isStopped) {
            val tick = rememberTick(ui.settings.haptics)
            Box(
                Modifier
                    .align(Alignment.TopEnd)
                    .padding(6.dp)
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(Color.Black.copy(alpha = 0.55f))
                    .clickable(onClickLabel = stringResource(R.string.action_stop)) {
                        tick()
                        onStop()
                    },
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    Icons.Default.Stop,
                    contentDescription = stringResource(R.string.action_stop),
                    tint = Color.White,
                    modifier = Modifier.size(22.dp),
                )
            }
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

/** Moonlight shows another computer's screen on the TV: what is on it now, and the way back to the movie. */
@Composable
internal fun StreamingCard(host: String, haptics: Boolean, onEnd: () -> Unit) {
    val tick = rememberTick(haptics)
    Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Tv, null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.moonlight_streaming_title), style = MaterialTheme.typography.titleMedium)
            }
            if (host.isNotEmpty()) {
                Spacer(Modifier.height(6.dp))
                Text(
                    stringResource(R.string.moonlight_streaming_host, host),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Spacer(Modifier.height(12.dp))
            Button(onClick = { tick(); onEnd() }) { LabelText(stringResource(R.string.action_moonlight_stop)) }
        }
    }
}

/**
 * The PC does not answer ([RemoteUiState.offline], already while reconnecting): "Wake up the PC" when the companion
 * said how ([PcWaker]) — while it wakes the card says so, and when the PC did not answer it says what to check — and
 * "Connect" (the connection screen).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun OfflineCard(ui: RemoteUiState, onOpenConnection: () -> Unit, onWake: () -> Unit = {}) {
    if (!ui.offline && ui.wake != PcWaker.State.WAKING) return
    Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
        Column(Modifier.padding(16.dp)) {
            Text(
                when {
                    ui.hostName == null -> stringResource(R.string.no_server_configured)
                    ui.asleep -> stringResource(R.string.pc_asleep, ui.hostName)
                    else -> stringResource(R.string.no_link_with, ui.hostName)
                },
                style = MaterialTheme.typography.titleMedium,
            )
            when (ui.wake) {
                PcWaker.State.WAKING -> Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(8.dp))
                    Text(stringResource(R.string.waking_pc, ui.hostName.orEmpty()), style = MaterialTheme.typography.bodyMedium)
                }
                PcWaker.State.FAILED -> Text(
                    stringResource(R.string.wake_pc_failed),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(top = 8.dp),
                )
                else -> Unit
            }
            Spacer(Modifier.height(8.dp))
            // Waking is what a sleeping PC needs, so it comes first; the connection screen is for anything else.
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                val canWake = ui.wake != null && ui.wake != PcWaker.State.WAKING
                if (canWake) {
                    Button(onClick = onWake) {
                        Icon(Icons.Default.PowerSettingsNew, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        LabelText(stringResource(R.string.action_wake_pc))
                    }
                    OutlinedButton(onClick = onOpenConnection) { LabelText(stringResource(R.string.action_connect)) }
                } else {
                    Button(onClick = onOpenConnection) { LabelText(stringResource(R.string.action_connect)) }
                }
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
