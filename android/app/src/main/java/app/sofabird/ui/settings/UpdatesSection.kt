package app.sofabird.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.data.update.AvailableUpdate
import app.sofabird.data.update.UpdateChannel
import app.sofabird.data.update.UpdateState
import app.sofabird.data.update.update
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.update.UpdateProgress

/** [enabled] — this build updates itself (a debug or a `-local` build does not). */
data class UpdatesUi(val enabled: Boolean, val channel: UpdateChannel, val state: UpdateState)

/** Settings → "Updates": the channel, where the update is, "Check now" / "Update". */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun UpdatesSection(
    ui: UpdatesUi,
    onChannel: (UpdateChannel) -> Unit,
    onCheck: () -> Unit,
    onInstall: (AvailableUpdate) -> Unit,
    onWhatsNew: (AvailableUpdate) -> Unit,
) {
    Section(stringResource(R.string.settings_updates))
    if (!ui.enabled) {
        Text(
            stringResource(R.string.updates_disabled),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        return
    }
    SwitchRow(
        title = stringResource(R.string.updates_test_builds),
        subtitle = stringResource(R.string.updates_test_builds_hint),
        checked = ui.channel == UpdateChannel.TEST,
    ) { on -> onChannel(if (on) UpdateChannel.TEST else UpdateChannel.STABLE) }
    val state = ui.state
    val update = state.update
    Column(Modifier.padding(vertical = 4.dp)) {
        when (state) {
            UpdateState.Idle -> Unit
            UpdateState.Checking -> {
                Text(stringResource(R.string.updates_checking), style = MaterialTheme.typography.bodyMedium)
                LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 4.dp))
            }
            UpdateState.UpToDate -> Text(stringResource(R.string.updates_latest), style = MaterialTheme.typography.bodyMedium)
            UpdateState.CheckFailed -> Text(
                stringResource(R.string.updates_check_failed),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error,
            )
            else -> if (update != null) {
                Text(stringResource(R.string.updates_available, update.version), style = MaterialTheme.typography.bodyMedium)
                if (state !is UpdateState.Available) UpdateProgress(state)
            }
        }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 4.dp)) {
            when (state) {
                UpdateState.Idle, UpdateState.UpToDate, UpdateState.CheckFailed -> OutlinedButton(onClick = onCheck) {
                    LabelText(stringResource(R.string.updates_check))
                }
                is UpdateState.Available, is UpdateState.NeedsPermission, is UpdateState.InstallFailed -> if (update != null) {
                    OutlinedButton(onClick = { onInstall(update) }) { LabelText(stringResource(R.string.updates_install)) }
                    TextButton(onClick = { onWhatsNew(update) }) { LabelText(stringResource(R.string.updates_whats_new)) }
                }
                UpdateState.Checking, is UpdateState.Downloading, is UpdateState.Installing -> Unit
            }
        }
    }
}
