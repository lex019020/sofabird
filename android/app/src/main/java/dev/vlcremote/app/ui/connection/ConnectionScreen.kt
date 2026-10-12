package dev.vlcremote.app.ui.connection

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.data.discovery.DiscoveredHost
import dev.vlcremote.app.data.player.TestResult
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.ui.common.hint
import dev.vlcremote.app.ui.common.message
import dev.vlcremote.app.ui.connectionSummary
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
            contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            if (ui.activeServerId != null) {
                item { CurrentConnectionCard(ui, onDisconnect = viewModel::disconnect) }
            }

            if (ui.saved.isNotEmpty()) {
                item { SectionHeader(stringResource(R.string.saved_pcs)) }
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

            item {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    SectionHeader(stringResource(R.string.found_pcs), Modifier.weight(1f))
                    if (ui.scanning || ui.sweeping) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                }
            }
            if (ui.discovered.isEmpty()) {
                item {
                    Text(
                        stringResource(if (ui.scanning) R.string.searching else R.string.nothing_found),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }
            items(ui.discovered, key = { "found-" + it.host }) { host ->
                DiscoveredRow(host, onClick = { viewModel.pick(host) })
            }
            item {
                OutlinedButton(onClick = viewModel::sweepSubnet, enabled = !ui.sweeping) {
                    Text(stringResource(if (ui.sweeping) R.string.sweeping else R.string.sweep_subnet))
                }
                Text(
                    stringResource(R.string.sweep_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
            item { SectionHeader(stringResource(R.string.manual_entry)) }
            item { ManualForm(ui, viewModel, onConnected) }

            item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
            item { Instructions(ui.showInstructions, viewModel::toggleInstructions) }
            item { Spacer(Modifier.height(24.dp)) }
        }
    }
}

@Composable
private fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = modifier.padding(top = 8.dp, bottom = 4.dp))
}

@Composable
private fun CurrentConnectionCard(ui: ConnectionUiState, onDisconnect: () -> Unit) {
    val server = ui.saved.firstOrNull { it.id == ui.activeServerId }
    val (color, text) = connectionSummary(ui.connection, server?.displayName)
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            dev.vlcremote.app.ui.common.StatusDot(color)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(server?.displayName ?: "", style = MaterialTheme.typography.titleMedium)
                Text(text, style = MaterialTheme.typography.bodySmall)
            }
            TextButton(onClick = onDisconnect) { Text(stringResource(R.string.action_disconnect)) }
        }
    }
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
                    "${server.host}:${if (server.isCompanion) server.wsPort else server.httpPort} · " +
                        stringResource(if (server.isCompanion) R.string.mode_companion else R.string.mode_direct),
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
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
                    Badge(stringResource(R.string.badge_pc_found))
                    if (host.hasCompanion) Badge(stringResource(R.string.badge_companion))
                    when (host.vlcRunning) {
                        true -> Badge(stringResource(R.string.badge_vlc_running) + (host.vlcVersion?.let { " $it" } ?: ""))
                        false -> Badge(stringResource(R.string.badge_vlc_not_running))
                        null -> Unit
                    }
                    if (host.needsPairing == true) Badge(stringResource(R.string.badge_needs_pairing), highlight = true)
                }
            }
        }
    }
}

@Composable
private fun Badge(text: String, highlight: Boolean = false) {
    Text(
        text,
        style = MaterialTheme.typography.labelSmall,
        color = if (highlight) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSecondaryContainer,
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
            ServerMode.COMPANION -> {
                if (f.hasToken) {
                    Text(stringResource(R.string.already_paired), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
                }
                OutlinedTextField(
                    value = f.pairCode,
                    onValueChange = { v -> viewModel.updateForm { it.copy(pairCode = v.filter(Char::isDigit).take(6)) } },
                    label = { Text(stringResource(if (f.hasToken) R.string.field_pair_code_again else R.string.field_pair_code)) },
                    supportingText = { Text(stringResource(R.string.pair_code_hint)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                )
            }
        }

        // Test result
        when (val t = ui.test) {
            TestState.Idle -> Unit
            TestState.Running -> Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.testing))
            }
            is TestState.Ok -> {
                val r = t.result
                val text = when (r) {
                    is TestResult.DirectOk -> stringResource(
                        R.string.test_ok_direct,
                        r.version ?: "VLC",
                        stringResource(if (r.level >= 1) R.string.level_lua else R.string.level_http),
                    )
                    is TestResult.CompanionOk -> stringResource(
                        R.string.test_ok_companion,
                        r.name,
                        stringResource(if (r.vlc.running) R.string.badge_vlc_running else R.string.badge_vlc_not_running),
                    )
                    is TestResult.Failed -> ""
                }
                Text(text, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.bodyMedium)
            }
            is TestState.Failed -> Column {
                Text(t.error.message(context), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium)
                t.error.hint(context)?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(
                onClick = viewModel::testConnection,
                enabled = f.host.isNotBlank() && ui.test != TestState.Running,
                modifier = Modifier.weight(1f),
            ) { Text(stringResource(R.string.action_test)) }
            Button(
                onClick = { viewModel.saveAndConnect(onConnected) },
                enabled = f.host.isNotBlank(),
                modifier = Modifier.weight(1f),
            ) { Text(stringResource(if (f.serverId != null) R.string.action_save_connect else R.string.action_connect)) }
        }
    }
}

@Composable
private fun Instructions(expanded: Boolean, onToggle: () -> Unit) {
    Column {
        AssistChip(onClick = onToggle, label = { Text(stringResource(if (expanded) R.string.instructions_hide else R.string.instructions_show)) })
        if (expanded) {
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
