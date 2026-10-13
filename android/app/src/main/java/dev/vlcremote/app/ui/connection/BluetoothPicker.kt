@file:OptIn(ExperimentalLayoutApi::class)

package dev.vlcremote.app.ui.connection

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.BluetoothDisabled
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.vlcremote.app.R
import dev.vlcremote.app.ui.common.BluetoothPermissionDeniedDialog
import dev.vlcremote.app.ui.common.BluetoothPermissionRationaleDialog
import dev.vlcremote.app.ui.common.rememberBluetoothRequests

/** Permission rationale → "turn Bluetooth on" → bonded devices (computers first). */
@Composable
internal fun BluetoothPicker(state: BtPickerState, viewModel: ConnectionViewModel) {
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
                                tint = if (device.isComputer) {
                                    MaterialTheme.colorScheme.primary
                                } else {
                                    MaterialTheme.colorScheme.onSurfaceVariant
                                },
                            )
                            Spacer(Modifier.width(16.dp))
                            Column(Modifier.weight(1f)) {
                                Text(
                                    device.displayName,
                                    style = MaterialTheme.typography.titleMedium,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                Text(
                                    device.address,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
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
