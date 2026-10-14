@file:OptIn(ExperimentalLayoutApi::class)

package app.sofabird.ui.connection

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.data.discovery.DiscoveredHost
import app.sofabird.data.player.PairingState
import app.sofabird.domain.model.SavedServer
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.VlcProcessStatus
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.StatusDot
import app.sofabird.ui.common.hint
import app.sofabird.ui.common.message
import app.sofabird.ui.common.withNameBreaks
import app.sofabird.ui.connectionSummary

@Composable
internal fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = modifier.padding(top = 8.dp, bottom = 4.dp))
}

@Composable
internal fun PairingProgressCard(state: PairingState.Connecting, onCancel: () -> Unit) {
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 3.dp)
            Spacer(Modifier.width(16.dp))
            Text(
                stringResource(
                    when {
                        state.auto -> R.string.pairing_auto_found
                        state.target.viaBluetooth -> R.string.pairing_connecting_bt
                        else -> R.string.pairing_connecting
                    },
                    state.target.name,
                ),
                style = MaterialTheme.typography.titleSmall,
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = onCancel) { Text(stringResource(R.string.cancel)) }
        }
    }
}

@Composable
internal fun PairingFailedCard(state: PairingState.Failed, onRetry: () -> Unit, onClose: () -> Unit) {
    val context = LocalContext.current
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer),
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(stringResource(R.string.pairing_failed_title, state.target.name), style = MaterialTheme.typography.titleSmall)
            Spacer(Modifier.height(4.dp))
            Text(state.error.message(context), style = MaterialTheme.typography.bodyMedium)
            state.error.hint(context)?.let {
                Spacer(Modifier.height(4.dp))
                Text(it, style = MaterialTheme.typography.bodySmall)
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = onClose) { Text(stringResource(R.string.close)) }
                TextButton(onClick = onRetry) { Text(stringResource(R.string.retry)) }
            }
        }
    }
}

@Composable
internal fun CurrentConnectionCard(ui: ConnectionUiState, onDisconnect: () -> Unit, onEnterCode: () -> Unit) {
    val server = ui.activeServer ?: return
    val (color, text) = connectionSummary(ui.connection)
    Card(Modifier.fillMaxWidth()) {
        // The action sits under the name, not beside it: a PC name is one long word and must keep the full width.
        Column(Modifier.padding(start = 16.dp, top = 16.dp, end = 8.dp, bottom = 4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(end = 8.dp)) {
                StatusDot(color)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    LabelText(server.displayName, style = MaterialTheme.typography.titleMedium)
                    Text(text, style = MaterialTheme.typography.bodySmall)
                }
            }
            val c = ui.connection
            Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) {
                if (server.isCompanion && c is ConnectionState.Failed && c.error == ConnectionError.PairingRequired) {
                    TextButton(onClick = onEnterCode) { LabelText(stringResource(R.string.action_enter_code)) }
                } else {
                    TextButton(onClick = onDisconnect) { LabelText(stringResource(R.string.action_disconnect)) }
                }
            }
        }
    }
}

@Composable
internal fun NearbyPcCard(
    pc: NearbyPc,
    active: Boolean,
    busy: Boolean,
    onClick: () -> Unit,
    onForget: (() -> Unit)?,
) {
    val host = pc.host
    var menu by remember { mutableStateOf(false) }
    ElevatedCard(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            Modifier.padding(start = 20.dp, top = 20.dp, bottom = 20.dp, end = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                Modifier
                    .size(52.dp)
                    .background(MaterialTheme.colorScheme.primaryContainer, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.Default.Computer, null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(28.dp))
            }
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f)) {
                // A PC name is one long word: one line, growing with the font only like a label.
                LabelText(host.displayName, style = MaterialTheme.typography.titleMedium)
                val status = host.vlcStatus ?: host.vlcRunning?.let { if (it) VlcProcessStatus.RUNNING else VlcProcessStatus.NOT_RUNNING }
                vlcStatusLabel(status)?.let { label ->
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                        StatusDot(vlcStatusColor(status))
                        Spacer(Modifier.width(6.dp))
                        Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                    modifier = Modifier.padding(top = 6.dp),
                ) {
                    if (active) Badge(stringResource(R.string.badge_connected), highlight = true)
                    if (pc.paired) Badge(stringResource(R.string.badge_paired), highlight = !active)
                }
                if (!pc.paired) {
                    Text(
                        stringResource(R.string.nearby_tap_to_pair),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }
            if (busy) {
                CircularProgressIndicator(Modifier.padding(horizontal = 12.dp).size(24.dp), strokeWidth = 3.dp)
            } else if (onForget != null) {
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, stringResource(R.string.more_actions)) }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.forget_pc)) },
                            leadingIcon = { Icon(Icons.Default.Delete, null) },
                            onClick = {
                                menu = false
                                onForget()
                            },
                        )
                    }
                }
            }
        }
    }
}

@Composable
internal fun vlcStatusLabel(status: VlcProcessStatus?): String? = when (status) {
    VlcProcessStatus.RUNNING -> stringResource(R.string.badge_vlc_running)
    VlcProcessStatus.NOT_RUNNING -> stringResource(R.string.badge_vlc_not_running)
    VlcProcessStatus.NO_CONTROL -> stringResource(R.string.badge_vlc_no_control)
    VlcProcessStatus.STARTING -> stringResource(R.string.badge_vlc_starting)
    VlcProcessStatus.NOT_INSTALLED -> stringResource(R.string.badge_vlc_not_installed)
    VlcProcessStatus.UNKNOWN, null -> null
}

@Composable
internal fun vlcStatusColor(status: VlcProcessStatus?): Color = when (status) {
    VlcProcessStatus.RUNNING -> Color(0xFF4CAF50)
    VlcProcessStatus.STARTING -> MaterialTheme.colorScheme.tertiary
    VlcProcessStatus.NO_CONTROL -> Color(0xFFFFC107)
    VlcProcessStatus.NOT_INSTALLED -> MaterialTheme.colorScheme.error
    else -> Color.Gray
}

@Composable
internal fun SavedServerRow(server: SavedServer, active: Boolean, onConnect: () -> Unit, onEdit: () -> Unit, onForget: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Row(
            Modifier
                .clickable(onClick = onConnect)
                .padding(start = 16.dp, top = 8.dp, bottom = 8.dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.Default.Computer,
                null,
                tint = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                LabelText(server.displayName, style = MaterialTheme.typography.titleSmall)
                Text(
                    if (server.isCompanion && !server.hasLanAddress) {
                        stringResource(R.string.saved_via_bluetooth, server.btAddress.orEmpty()) + " · " +
                            stringResource(R.string.mode_companion)
                    } else {
                        "${server.host}:${if (server.isCompanion) server.wsPort else server.httpPort}".withNameBreaks() + " · " +
                            stringResource(if (server.isCompanion) R.string.mode_companion else R.string.mode_direct) +
                            (
                                if (server.isCompanion && server.btAddress != null) {
                                    " · " + stringResource(R.string.transport_bluetooth)
                                } else {
                                    ""
                                }
                                )
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            IconButton(onClick = onEdit) { Icon(Icons.Default.Edit, stringResource(R.string.edit)) }
            IconButton(onClick = onForget) { Icon(Icons.Default.Delete, stringResource(R.string.forget)) }
        }
    }
}

@Composable
internal fun BluetoothEntry(busy: Boolean, onClick: () -> Unit) {
    Column {
        OutlinedButton(onClick = onClick, enabled = !busy) {
            Icon(Icons.Default.Bluetooth, null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(stringResource(R.string.bt_connect_action))
        }
        Text(
            stringResource(R.string.bt_connect_hint),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
internal fun OtherWaysHeader(expanded: Boolean, onToggle: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(top = 8.dp)
            .clickable(onClick = onToggle)
            .padding(vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(stringResource(R.string.other_ways), style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.other_ways_hint),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Icon(
            if (expanded) Icons.Default.ExpandLess else Icons.Default.ExpandMore,
            contentDescription = stringResource(if (expanded) R.string.collapse else R.string.expand),
        )
    }
}

@Composable
internal fun DiscoveredRow(host: DiscoveredHost, onClick: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Row(
            Modifier
                .clickable(onClick = onClick)
                .padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Default.Computer, null)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                LabelText(host.displayName, style = MaterialTheme.typography.titleSmall)
                Text(
                    host.host.withNameBreaks(),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                    modifier = Modifier.padding(top = 4.dp),
                ) {
                    Badge(stringResource(R.string.badge_pc_found))
                    if (host.vlcRunning == true) {
                        Badge(stringResource(R.string.badge_vlc_running) + (host.vlcVersion?.let { " $it" } ?: ""))
                    }
                }
            }
        }
    }
}

@Composable
internal fun Badge(text: String, highlight: Boolean = false) {
    LabelText(
        text,
        style = MaterialTheme.typography.labelSmall,
        color = if (highlight) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSecondaryContainer,
        modifier = Modifier
            .background(
                if (highlight) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.secondaryContainer,
                RoundedCornerShape(6.dp),
            )
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}
