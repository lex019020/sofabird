@file:OptIn(ExperimentalLayoutApi::class)

package dev.vlcremote.app.ui.connection

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.BluetoothDisabled
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.data.discovery.DiscoveredHost
import dev.vlcremote.app.data.player.PairingState
import dev.vlcremote.app.data.player.TestResult
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.ui.common.BluetoothPermissionDeniedDialog
import dev.vlcremote.app.ui.common.BluetoothPermissionRationaleDialog
import dev.vlcremote.app.ui.common.LabelText
import dev.vlcremote.app.ui.common.StatusDot
import dev.vlcremote.app.ui.common.rememberBluetoothRequests
import dev.vlcremote.app.ui.common.hint
import dev.vlcremote.app.ui.common.message
import dev.vlcremote.app.ui.connectionSummary
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.VlcProcessStatus
import org.koin.androidx.compose.koinViewModel

@Composable
fun ConnectionScreen(
    onConnected: () -> Unit,
    onBack: () -> Unit,
    viewModel: ConnectionViewModel = koinViewModel(),
) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()

    DisposableEffect(Unit) {
        viewModel.startDiscovery()
        onDispose { viewModel.stopDiscovery() }
    }

    // Back from the system Bluetooth settings: the list of bonded devices may have changed.
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { viewModel.refreshBluetoothPicker() }

    // Connected (and paired) through the flow → straight to the remote.
    LaunchedEffect(ui.pairing) {
        if (ui.pairing is PairingState.Done) {
            viewModel.pairingDoneHandled()
            onConnected()
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.connection_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back)) }
                },
            )
        },
    ) { padding ->
        LazyColumn(
            Modifier
                .fillMaxSize()
                .padding(padding),
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            when (val p = ui.pairing) {
                is PairingState.Connecting -> item(key = "pairing") { PairingProgressCard(p, onCancel = viewModel::cancelPairing) }
                is PairingState.Failed -> item(key = "pairing") {
                    PairingFailedCard(p, onRetry = viewModel::retryPairing, onClose = viewModel::cancelPairing)
                }
                else -> Unit
            }

            if (ui.activeServer != null) {
                item(key = "current") {
                    CurrentConnectionCard(
                        ui,
                        onDisconnect = viewModel::disconnect,
                        onEnterCode = { ui.activeServer?.let { viewModel.connectSaved(it, onConnected) } },
                    )
                }
            }

            // ---------------------------------------------------------------- nearby companions
            item(key = "nearby-header") {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    SectionHeader(stringResource(R.string.nearby_pcs), Modifier.weight(1f))
                    if (ui.scanning) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                }
            }
            if (ui.nearby.isEmpty()) {
                item(key = "nearby-empty") {
                    Text(
                        stringResource(if (ui.searchTimedOut) R.string.nearby_not_found else R.string.searching),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }
            items(ui.nearby, key = { "nearby-" + it.host.host }) { pc ->
                val busy = (ui.pairing as? PairingState.Connecting)?.target?.host == pc.host.host
                NearbyPcCard(
                    pc = pc,
                    active = pc.saved != null && pc.saved.id == ui.activeServerId && ui.connection.isOnline,
                    busy = busy,
                    onClick = { viewModel.connectNearby(pc) },
                    onForget = pc.saved?.let { saved -> { viewModel.forget(saved) } },
                )
            }

            // ---------------------------------------------------------------- saved, not around
            if (ui.saved.isNotEmpty()) {
                item(key = "saved-header") { SectionHeader(stringResource(R.string.saved_pcs)) }
                items(ui.saved, key = { "saved-" + it.id }) { server ->
                    SavedServerRow(
                        server = server,
                        active = server.id == ui.activeServerId,
                        onConnect = { viewModel.connectSaved(server, onConnected) },
                        onEdit = { viewModel.edit(server) },
                        onForget = { viewModel.forget(server) },
                    )
                }
            }

            // ---------------------------------------------------------------- other ways
            item(key = "other-header") {
                OtherWaysHeader(ui.otherOptionsExpanded) { viewModel.setOtherOptionsExpanded(!ui.otherOptionsExpanded) }
            }
            if (ui.otherOptionsExpanded) {
                item(key = "bluetooth") {
                    BluetoothEntry(
                        busy = (ui.pairing as? PairingState.Connecting)?.target?.viaBluetooth == true,
                        onClick = viewModel::openBluetoothPicker,
                    )
                }
                item(key = "bt-divider") { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
                item(key = "other-found-header") {
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                        SectionHeader(stringResource(R.string.found_pcs), Modifier.weight(1f))
                        if (ui.sweeping) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    }
                }
                items(ui.otherHosts, key = { "found-" + it.host }) { host ->
                    DiscoveredRow(host, onClick = { viewModel.pick(host) })
                }
                item(key = "sweep") {
                    Column {
                        OutlinedButton(onClick = viewModel::sweepSubnet, enabled = !ui.sweeping) {
                            Text(stringResource(if (ui.sweeping) R.string.sweeping else R.string.sweep_subnet))
                        }
                        Text(
                            stringResource(R.string.sweep_hint),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                item(key = "manual-divider") { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
                item(key = "manual-header") { SectionHeader(stringResource(R.string.manual_entry)) }
                item(key = "manual") { ManualForm(ui, viewModel, onConnected) }
                item(key = "instr-divider") { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
                item(key = "instructions") { Instructions(ui.showInstructions, viewModel::toggleInstructions) }
            }
            item(key = "bottom") { Spacer(Modifier.height(24.dp)) }
        }
    }

    BluetoothPicker(ui.btPicker, viewModel)

    (ui.pairing as? PairingState.EnterCode)?.let { state ->
        PairCodeDialog(
            state = state,
            onSubmit = viewModel::submitPairCode,
            onCancel = viewModel::cancelPairing,
        )
    }
}

@Composable
private fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = modifier.padding(top = 8.dp, bottom = 4.dp))
}

@Composable
private fun PairingProgressCard(state: PairingState.Connecting, onCancel: () -> Unit) {
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
private fun PairingFailedCard(state: PairingState.Failed, onRetry: () -> Unit, onClose: () -> Unit) {
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
private fun CurrentConnectionCard(ui: ConnectionUiState, onDisconnect: () -> Unit, onEnterCode: () -> Unit) {
    val server = ui.activeServer ?: return
    val (color, text) = connectionSummary(ui.connection, server.displayName)
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            StatusDot(color)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(server.displayName, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(text, style = MaterialTheme.typography.bodySmall)
            }
            val c = ui.connection
            if (server.isCompanion && c is ConnectionState.Failed && c.error == ConnectionError.PairingRequired) {
                TextButton(onClick = onEnterCode) { LabelText(stringResource(R.string.action_enter_code)) }
            } else {
                TextButton(onClick = onDisconnect) { LabelText(stringResource(R.string.action_disconnect)) }
            }
        }
    }
}

@Composable
private fun NearbyPcCard(
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
                Text(
                    host.displayName,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
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
private fun vlcStatusLabel(status: VlcProcessStatus?): String? = when (status) {
    VlcProcessStatus.RUNNING -> stringResource(R.string.badge_vlc_running)
    VlcProcessStatus.NOT_RUNNING -> stringResource(R.string.badge_vlc_not_running)
    VlcProcessStatus.NO_CONTROL -> stringResource(R.string.badge_vlc_no_control)
    VlcProcessStatus.STARTING -> stringResource(R.string.badge_vlc_starting)
    VlcProcessStatus.NOT_INSTALLED -> stringResource(R.string.badge_vlc_not_installed)
    VlcProcessStatus.UNKNOWN, null -> null
}

@Composable
private fun vlcStatusColor(status: VlcProcessStatus?): Color = when (status) {
    VlcProcessStatus.RUNNING -> Color(0xFF4CAF50)
    VlcProcessStatus.STARTING -> MaterialTheme.colorScheme.tertiary
    VlcProcessStatus.NO_CONTROL -> Color(0xFFFFC107)
    VlcProcessStatus.NOT_INSTALLED -> MaterialTheme.colorScheme.error
    else -> Color.Gray
}

@Composable
private fun SavedServerRow(server: SavedServer, active: Boolean, onConnect: () -> Unit, onEdit: () -> Unit, onForget: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Row(
            Modifier
                .clickable(onClick = onConnect)
                .padding(start = 16.dp, top = 8.dp, bottom = 8.dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Default.Computer, null, tint = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(server.displayName, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    if (server.isCompanion && !server.hasLanAddress) {
                        stringResource(R.string.saved_via_bluetooth, server.btAddress.orEmpty()) + " · " +
                            stringResource(R.string.mode_companion)
                    } else {
                        "${server.host}:${if (server.isCompanion) server.wsPort else server.httpPort} · " +
                            stringResource(if (server.isCompanion) R.string.mode_companion else R.string.mode_direct) +
                            (if (server.isCompanion && server.btAddress != null) " · " + stringResource(R.string.transport_bluetooth) else "")
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
private fun BluetoothEntry(busy: Boolean, onClick: () -> Unit) {
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

/** Permission rationale → "turn Bluetooth on" → bonded devices (computers first). */
@Composable
private fun BluetoothPicker(state: BtPickerState, viewModel: ConnectionViewModel) {
    val requests = rememberBluetoothRequests(
        onPermissionResult = viewModel::onBluetoothPermissionResult,
        onEnableResult = viewModel::onBluetoothEnableResult,
    )
    when (state) {
        BtPickerState.Hidden -> Unit
        BtPickerState.NeedsPermission -> BluetoothPermissionRationaleDialog(
            onAllow = requests.requestPermission,
            onDismiss = viewModel::closeBluetoothPicker,
        )
        BtPickerState.PermissionDenied -> BluetoothPermissionDeniedDialog(
            onOpenSettings = {
                viewModel.closeBluetoothPicker()
                requests.openAppSettings()
            },
            onDismiss = viewModel::closeBluetoothPicker,
        )
        BtPickerState.Off -> AlertDialog(
            onDismissRequest = viewModel::closeBluetoothPicker,
            icon = { Icon(Icons.Default.BluetoothDisabled, null) },
            title = { Text(stringResource(R.string.bt_off_title)) },
            text = { Text(stringResource(R.string.bt_off_body)) },
            confirmButton = { TextButton(onClick = requests.requestEnable) { Text(stringResource(R.string.bt_turn_on)) } },
            dismissButton = { TextButton(onClick = viewModel::closeBluetoothPicker) { Text(stringResource(R.string.cancel)) } },
        )
        BtPickerState.Unsupported -> AlertDialog(
            onDismissRequest = viewModel::closeBluetoothPicker,
            title = { Text(stringResource(R.string.bt_unsupported)) },
            confirmButton = { TextButton(onClick = viewModel::closeBluetoothPicker) { Text(stringResource(R.string.close)) } },
        )
        is BtPickerState.Devices -> ModalBottomSheet(onDismissRequest = viewModel::closeBluetoothPicker) {
            Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
                Text(stringResource(R.string.bt_picker_title), style = MaterialTheme.typography.titleLarge)
                Text(
                    stringResource(if (state.devices.isEmpty()) R.string.bt_picker_empty else R.string.bt_picker_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp, bottom = 8.dp),
                )
                LazyColumn(Modifier.heightIn(max = 420.dp)) {
                    items(state.devices, key = { it.address }) { device ->
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .clickable { viewModel.connectBluetooth(device) }
                                .padding(vertical = 12.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Icon(
                                if (device.isComputer) Icons.Default.Computer else Icons.Default.Bluetooth,
                                null,
                                tint = if (device.isComputer) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Spacer(Modifier.width(16.dp))
                            Column(Modifier.weight(1f)) {
                                Text(device.displayName, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(device.address, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                }
                TextButton(onClick = requests.openBluetoothSettings, modifier = Modifier.padding(top = 4.dp)) {
                    Text(stringResource(R.string.bt_open_settings))
                }
            }
        }
    }
}

@Composable
private fun OtherWaysHeader(expanded: Boolean, onToggle: () -> Unit) {
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
private fun DiscoveredRow(host: DiscoveredHost, onClick: () -> Unit) {
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
                Text(host.displayName, style = MaterialTheme.typography.titleSmall)
                Text(host.host, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(4.dp), modifier = Modifier.padding(top = 4.dp)) {
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
private fun Badge(text: String, highlight: Boolean = false) {
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

@Composable
private fun ManualForm(ui: ConnectionUiState, viewModel: ConnectionViewModel, onConnected: () -> Unit) {
    val f = ui.form
    val context = LocalContext.current
    var showPassword by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(
                selected = f.mode == ServerMode.DIRECT,
                onClick = { viewModel.setMode(ServerMode.DIRECT) },
                label = { Text(stringResource(R.string.mode_direct)) },
            )
            FilterChip(
                selected = f.mode == ServerMode.COMPANION,
                onClick = { viewModel.setMode(ServerMode.COMPANION) },
                label = { Text(stringResource(R.string.mode_companion)) },
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = f.host,
                onValueChange = { v -> viewModel.updateForm { it.copy(host = v) } },
                label = { Text(stringResource(R.string.field_host)) },
                singleLine = true,
                modifier = Modifier.weight(2f),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            )
            OutlinedTextField(
                value = f.port,
                onValueChange = { v -> viewModel.updateForm { it.copy(port = v.filter(Char::isDigit).take(5)) } },
                label = { Text(stringResource(R.string.field_port)) },
                singleLine = true,
                modifier = Modifier.weight(1f),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            )
        }
        OutlinedTextField(
            value = f.name,
            onValueChange = { v -> viewModel.updateForm { it.copy(name = v) } },
            label = { Text(stringResource(R.string.field_name_optional)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        when (f.mode) {
            ServerMode.DIRECT -> OutlinedTextField(
                value = f.password,
                onValueChange = { v -> viewModel.updateForm { it.copy(password = v) } },
                label = { Text(stringResource(R.string.field_password)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
                visualTransformation = if (showPassword) VisualTransformation.None else PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                trailingIcon = {
                    IconButton(onClick = { showPassword = !showPassword }) {
                        Icon(if (showPassword) Icons.Default.VisibilityOff else Icons.Default.Visibility, null)
                    }
                },
            )
            ServerMode.COMPANION -> Text(
                stringResource(if (f.hasToken) R.string.already_paired else R.string.manual_companion_hint),
                style = MaterialTheme.typography.bodySmall,
                color = if (f.hasToken) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        // Test result (direct mode)
        when (val t = ui.test) {
            TestState.Idle -> Unit
            TestState.Running -> Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.testing))
            }
            is TestState.Ok -> (t.result as? TestResult.DirectOk)?.let { r ->
                Text(
                    stringResource(
                        R.string.test_ok_direct,
                        r.version ?: "VLC",
                        stringResource(if (r.level >= 1) R.string.level_lua else R.string.level_http),
                    ),
                    color = MaterialTheme.colorScheme.primary,
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
            is TestState.Failed -> Column {
                Text(t.error.message(context), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium)
                t.error.hint(context)?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (f.mode == ServerMode.DIRECT) {
                OutlinedButton(
                    onClick = viewModel::testConnection,
                    enabled = f.host.isNotBlank() && ui.test != TestState.Running,
                    modifier = Modifier.weight(1f),
                ) { Text(stringResource(R.string.action_test)) }
            }
            Button(
                onClick = { viewModel.saveAndConnect(onConnected) },
                enabled = f.host.isNotBlank() && ui.pairing !is PairingState.Connecting,
                modifier = Modifier.weight(1f),
            ) { Text(stringResource(if (f.serverId != null) R.string.action_save_connect else R.string.action_connect)) }
        }
    }
}

@Composable
private fun Instructions(expanded: Boolean, onToggle: () -> Unit) {
    Column {
        AssistChip(onClick = onToggle, label = { Text(stringResource(if (expanded) R.string.instructions_hide else R.string.instructions_show)) })
        AnimatedVisibility(expanded) {
            Column {
                Spacer(Modifier.height(8.dp))
                Text(stringResource(R.string.instructions_title), style = MaterialTheme.typography.titleSmall)
                Spacer(Modifier.height(4.dp))
                listOf(
                    R.string.instructions_step1,
                    R.string.instructions_step2,
                    R.string.instructions_step3,
                    R.string.instructions_step4,
                    R.string.instructions_step5,
                    R.string.instructions_step6,
                ).forEachIndexed { i, res ->
                    Row(Modifier.padding(vertical = 3.dp)) {
                        Text("${i + 1}.", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.width(24.dp))
                        Text(stringResource(res), style = MaterialTheme.typography.bodyMedium)
                    }
                }
                Spacer(Modifier.height(8.dp))
                Text(stringResource(R.string.instructions_lua), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Text(stringResource(R.string.instructions_companion), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

/** Error text for the code dialog (shorter than the connection-screen messages). */
@Composable
internal fun pairCodeErrorText(error: ConnectionError): String {
    val context = LocalContext.current
    return when (error) {
        ConnectionError.InvalidPairCode -> stringResource(R.string.err_invalid_pair_code)
        else -> error.message(context)
    }
}
