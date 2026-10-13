@file:OptIn(ExperimentalLayoutApi::class)

package dev.vlcremote.app.ui.connection

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.unit.dp
import dev.vlcremote.app.R
import dev.vlcremote.app.data.player.PairingState
import dev.vlcremote.app.data.player.TestResult
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.ui.common.hint
import dev.vlcremote.app.ui.common.message
import dev.vlcremote.protocol.model.ConnectionError

@Composable
internal fun ManualForm(ui: ConnectionUiState, actions: ConnectionActions) {
    val f = ui.form
    val context = LocalContext.current
    var showPassword by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(
                selected = f.mode == ServerMode.DIRECT,
                onClick = { actions.setMode(ServerMode.DIRECT) },
                label = { Text(stringResource(R.string.mode_direct)) },
            )
            FilterChip(
                selected = f.mode == ServerMode.COMPANION,
                onClick = { actions.setMode(ServerMode.COMPANION) },
                label = { Text(stringResource(R.string.mode_companion)) },
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = f.host,
                onValueChange = { v -> actions.updateForm { it.copy(host = v) } },
                label = { Text(stringResource(R.string.field_host)) },
                singleLine = true,
                modifier = Modifier.weight(2f),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            )
            OutlinedTextField(
                value = f.port,
                onValueChange = { v -> actions.updateForm { it.copy(port = v.filter(Char::isDigit).take(5)) } },
                label = { Text(stringResource(R.string.field_port)) },
                singleLine = true,
                modifier = Modifier.weight(1f),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            )
        }
        OutlinedTextField(
            value = f.name,
            onValueChange = { v -> actions.updateForm { it.copy(name = v) } },
            label = { Text(stringResource(R.string.field_name_optional)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        when (f.mode) {
            ServerMode.DIRECT -> OutlinedTextField(
                value = f.password,
                onValueChange = { v -> actions.updateForm { it.copy(password = v) } },
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
                    onClick = actions::testConnection,
                    enabled = f.host.isNotBlank() && ui.test != TestState.Running,
                    modifier = Modifier.weight(1f),
                ) { Text(stringResource(R.string.action_test)) }
            }
            Button(
                onClick = actions::saveAndConnect,
                enabled = f.host.isNotBlank() && ui.pairing !is PairingState.Connecting,
                modifier = Modifier.weight(1f),
            ) { Text(stringResource(if (f.serverId != null) R.string.action_save_connect else R.string.action_connect)) }
        }
    }
}

@Composable
internal fun Instructions(expanded: Boolean, onToggle: () -> Unit) {
    Column {
        AssistChip(
            onClick = onToggle,
            label = { Text(stringResource(if (expanded) R.string.instructions_hide else R.string.instructions_show)) },
        )
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
                Text(
                    stringResource(R.string.instructions_lua),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    stringResource(R.string.instructions_companion),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
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
