@file:OptIn(ExperimentalLayoutApi::class)

package app.sofabird.ui.connection

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sofabird.R
import app.sofabird.data.player.PairingState
import app.sofabird.domain.model.SavedServer
import org.koin.androidx.compose.koinViewModel

/**
 * What the connection screen's content asks for. The screen routes it to the ViewModel; previews and screenshot
 * tests use the no-op defaults.
 */
interface ConnectionActions {
    fun back() {}
    fun cancelPairing() {}
    fun retryPairing() {}
    fun disconnect() {}
    fun connectSaved(server: SavedServer) {}
    fun connectNearby(pc: NearbyPc) {}
    fun edit(server: SavedServer) {}
    fun forget(server: SavedServer) {}
    fun setOtherOptionsExpanded(expanded: Boolean) {}
    fun openBluetoothPicker() {}
    fun toggleInstructions() {}
    fun updateForm(block: (ConnectionForm) -> ConnectionForm) {}
    fun saveAndConnect() {}
}

@Composable
fun ConnectionScreen(
    onConnected: () -> Unit,
    onBack: () -> Unit,
    viewModel: ConnectionViewModel = koinViewModel(),
) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val back by rememberUpdatedState(onBack)
    val actions = remember(viewModel) {
        object : ConnectionActions {
            override fun back() = back()
            override fun cancelPairing() = viewModel.cancelPairing()
            override fun retryPairing() = viewModel.retryPairing()
            override fun disconnect() = viewModel.disconnect()
            override fun connectSaved(server: SavedServer) = viewModel.connectSaved(server)
            override fun connectNearby(pc: NearbyPc) = viewModel.connectNearby(pc)
            override fun edit(server: SavedServer) = viewModel.edit(server)
            override fun forget(server: SavedServer) = viewModel.forget(server)
            override fun setOtherOptionsExpanded(expanded: Boolean) = viewModel.setOtherOptionsExpanded(expanded)
            override fun openBluetoothPicker() = viewModel.openBluetoothPicker()
            override fun toggleInstructions() = viewModel.toggleInstructions()
            override fun updateForm(block: (ConnectionForm) -> ConnectionForm) = viewModel.updateForm(block)
            override fun saveAndConnect() = viewModel.saveAndConnect()
        }
    }

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

    ConnectionContent(ui, actions)

    BluetoothPicker(ui.btPicker, viewModel)

    (ui.pairing as? PairingState.EnterCode)?.let { state ->
        PairCodeDialog(
            state = state,
            onSubmit = viewModel::submitPairCode,
            onCancel = viewModel::cancelPairing,
        )
    }
}

/** The connection screen without its ViewModel: the current link, PCs nearby, saved PCs and the other ways. */
@Composable
fun ConnectionContent(ui: ConnectionUiState, actions: ConnectionActions) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.connection_title)) },
                navigationIcon = {
                    IconButton(onClick = actions::back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back)) }
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
                is PairingState.Connecting -> item(key = "pairing") { PairingProgressCard(p, onCancel = actions::cancelPairing) }
                is PairingState.Failed -> item(key = "pairing") {
                    PairingFailedCard(p, onRetry = actions::retryPairing, onClose = actions::cancelPairing)
                }
                else -> Unit
            }

            if (ui.activeServer != null) {
                item(key = "current") {
                    CurrentConnectionCard(
                        ui,
                        onDisconnect = actions::disconnect,
                        onEnterCode = { ui.activeServer?.let(actions::connectSaved) },
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
                    onClick = { actions.connectNearby(pc) },
                    onForget = pc.saved?.let { saved -> { actions.forget(saved) } },
                )
            }

            // ---------------------------------------------------------------- saved, not around
            if (ui.saved.isNotEmpty()) {
                item(key = "saved-header") { SectionHeader(stringResource(R.string.saved_pcs)) }
                items(ui.saved, key = { "saved-" + it.id }) { server ->
                    SavedServerRow(
                        server = server,
                        active = server.id == ui.activeServerId,
                        onConnect = { actions.connectSaved(server) },
                        onEdit = { actions.edit(server) },
                        onForget = { actions.forget(server) },
                    )
                }
            }

            // ---------------------------------------------------------------- other ways
            item(key = "other-header") {
                OtherWaysHeader(ui.otherOptionsExpanded) { actions.setOtherOptionsExpanded(!ui.otherOptionsExpanded) }
            }
            if (ui.otherOptionsExpanded) {
                item(key = "bluetooth") {
                    BluetoothEntry(
                        busy = (ui.pairing as? PairingState.Connecting)?.target?.viaBluetooth == true,
                        onClick = actions::openBluetoothPicker,
                    )
                }
                item(key = "manual-divider") { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
                item(key = "manual-header") { SectionHeader(stringResource(R.string.manual_entry)) }
                item(key = "manual") { ManualForm(ui, actions) }
                item(key = "instr-divider") { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
                item(key = "instructions") { Instructions(ui.showInstructions, actions::toggleInstructions) }
            }
            item(key = "bottom") { Spacer(Modifier.height(24.dp)) }
        }
    }
}
