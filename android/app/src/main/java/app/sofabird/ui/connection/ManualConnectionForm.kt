@file:OptIn(ExperimentalLayoutApi::class)

package app.sofabird.ui.connection

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.data.player.PairingState
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.ui.common.message

/** The companion's address typed by hand; "Connect" starts the same connect-or-pair flow as a found PC. */
@Composable
internal fun ManualForm(ui: ConnectionUiState, actions: ConnectionActions) {
    val f = ui.form

    // One field per line: beside the address, the port's five digits did not fit at a large font.
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(
            value = f.host,
            onValueChange = { v -> actions.updateForm { it.copy(host = v) } },
            label = { Text(stringResource(R.string.field_host)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        )
        OutlinedTextField(
            value = f.port,
            onValueChange = { v -> actions.updateForm { it.copy(port = v.filter(Char::isDigit).take(5)) } },
            label = { Text(stringResource(R.string.field_port)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(PORT_WIDTH_FRACTION),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
        )
        OutlinedTextField(
            value = f.name,
            onValueChange = { v -> actions.updateForm { it.copy(name = v) } },
            label = { Text(stringResource(R.string.field_name_optional)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Text(
            stringResource(if (f.hasToken) R.string.already_paired else R.string.manual_companion_hint),
            style = MaterialTheme.typography.bodySmall,
            color = if (f.hasToken) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Button(
            onClick = actions::saveAndConnect,
            enabled = f.host.isNotBlank() && ui.pairing !is PairingState.Connecting,
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(if (f.serverId != null) R.string.action_save_connect else R.string.action_connect)) }
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
                        Text(
                            "${i + 1}.",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.widthIn(min = 24.dp).padding(end = 6.dp),
                        )
                        Text(stringResource(res), style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
        }
    }
}

/** The port field's share of the width: five digits fit at any font scale. */
private const val PORT_WIDTH_FRACTION = 0.5f

/** Error text for the code dialog (shorter than the connection-screen messages). */
@Composable
internal fun pairCodeErrorText(error: ConnectionError): String {
    val context = LocalContext.current
    return when (error) {
        ConnectionError.InvalidPairCode -> stringResource(R.string.err_invalid_pair_code)
        else -> error.message(context)
    }
}
