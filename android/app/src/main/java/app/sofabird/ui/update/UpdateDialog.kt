package app.sofabird.ui.update

import android.text.format.Formatter
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sofabird.R
import app.sofabird.data.update.AppUpdates
import app.sofabird.data.update.AvailableUpdate
import app.sofabird.data.update.UpdateState
import app.sofabird.data.update.update
import app.sofabird.ui.common.LabelText
import org.koin.compose.koinInject

/** "Update available" over any screen, once per version (see [AppUpdates.promptOpen]). */
@Composable
fun UpdatePromptHost() {
    val updates: AppUpdates = koinInject()
    val open by updates.promptOpen.collectAsStateWithLifecycle()
    val state by updates.state.collectAsStateWithLifecycle()
    val update = state.update
    if (!open || update == null || state is UpdateState.Installing) return
    val uriHandler = LocalUriHandler.current
    UpdateDialog(
        state = state,
        onUpdate = { updates.install(update) },
        onLater = { updates.later(update) },
        onWhatsNew = { uriHandler.openUri(update.page) },
    )
}

@Composable
fun UpdateDialog(state: UpdateState, onUpdate: () -> Unit, onLater: () -> Unit, onWhatsNew: () -> Unit) {
    val update = state.update ?: return
    AlertDialog(
        onDismissRequest = onLater,
        title = { Text(stringResource(R.string.update_title)) },
        text = {
            Column {
                Text(updateText(update))
                Spacer(Modifier.height(12.dp))
                UpdateProgress(state)
                TextButton(onClick = onWhatsNew, modifier = Modifier.padding(top = 4.dp)) {
                    LabelText(stringResource(R.string.updates_whats_new))
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onUpdate, enabled = state !is UpdateState.Downloading) {
                LabelText(stringResource(R.string.updates_install))
            }
        },
        dismissButton = {
            TextButton(onClick = onLater) { LabelText(stringResource(R.string.updates_later)) }
        },
    )
}

/** What happens with the download: a progress bar, a failure, or what Android will ask. */
@Composable
fun UpdateProgress(state: UpdateState) {
    val hint = MaterialTheme.typography.bodySmall
    when (state) {
        is UpdateState.Downloading -> {
            val progress = state.progress
            if (progress != null) {
                LinearProgressIndicator(progress = { progress }, modifier = Modifier.fillMaxWidth())
                Text(stringResource(R.string.updates_downloading_percent, (progress * 100).toInt()), style = hint)
            } else {
                LinearProgressIndicator(Modifier.fillMaxWidth())
                Text(stringResource(R.string.updates_downloading), style = hint)
            }
        }
        is UpdateState.NeedsPermission ->
            Text(stringResource(R.string.updates_permission_hint), style = hint, color = MaterialTheme.colorScheme.error)
        is UpdateState.InstallFailed ->
            Text(stringResource(R.string.updates_install_failed), style = hint, color = MaterialTheme.colorScheme.error)
        is UpdateState.Installing -> Text(stringResource(R.string.updates_installing), style = hint)
        else -> Text(stringResource(R.string.update_confirm_hint), style = hint, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun updateText(update: AvailableUpdate): String =
    if (update.size > 0) {
        stringResource(R.string.update_text_size, update.version, Formatter.formatShortFileSize(LocalContext.current, update.size))
    } else {
        stringResource(R.string.update_text, update.version)
    }
