package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import dev.vlcremote.app.R
import dev.vlcremote.protocol.model.VlcProcessStatus

/**
 * Companion mode, VLC not controllable (`vlc.status` ≠ `running`): what is going on and the one
 * action that fixes it. Replaces the transport controls.
 */
@Composable
fun VlcStateCard(status: VlcProcessStatus, ui: RemoteUiState, onLaunch: () -> Unit) {
    val canLaunch = ui.capabilities.launchVlc && ui.connection.isOnline
    Card(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 12.dp),
    ) {
        Column(Modifier.padding(20.dp)) {
            when (status) {
                VlcProcessStatus.STARTING -> Progress(stringResource(R.string.vlc_state_starting))
                VlcProcessStatus.NOT_RUNNING, VlcProcessStatus.UNKNOWN -> {
                    Title(stringResource(R.string.vlc_not_running_title))
                    if (canLaunch) {
                        ActionButton(stringResource(R.string.action_launch_vlc), Icons.Default.PlayArrow, ui.launching, onLaunch)
                    } else {
                        Body(stringResource(R.string.vlc_state_not_running_manual))
                    }
                }
                VlcProcessStatus.NO_CONTROL -> {
                    Title(stringResource(R.string.vlc_state_no_control_title))
                    Body(stringResource(R.string.vlc_state_no_control_body))
                    if (canLaunch) {
                        Note(stringResource(R.string.vlc_state_no_control_note))
                        ActionButton(stringResource(R.string.action_restart_with_control), Icons.Default.RestartAlt, ui.launching, onLaunch)
                    } else {
                        Body(stringResource(R.string.vlc_state_no_control_manual))
                    }
                }
                VlcProcessStatus.NOT_INSTALLED -> {
                    Title(stringResource(R.string.vlc_state_not_installed_title))
                    Body(stringResource(R.string.vlc_state_not_installed_body))
                }
                VlcProcessStatus.RUNNING -> Unit
            }
        }
    }
}

@Composable
private fun Title(text: String) {
    Text(text, style = MaterialTheme.typography.titleLarge)
}

@Composable
private fun Body(text: String) {
    Spacer(Modifier.height(8.dp))
    Text(text, style = MaterialTheme.typography.bodyMedium)
}

@Composable
private fun Note(text: String) {
    Spacer(Modifier.height(6.dp))
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun Progress(text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator(Modifier.size(28.dp), strokeWidth = 3.dp)
        Spacer(Modifier.width(16.dp))
        Text(text, style = MaterialTheme.typography.titleLarge)
    }
}

@Composable
private fun ActionButton(text: String, icon: ImageVector, busy: Boolean, onClick: () -> Unit) {
    Spacer(Modifier.height(16.dp))
    Button(
        onClick = onClick,
        enabled = !busy,
        modifier = Modifier
            .fillMaxWidth()
            .height(56.dp),
        contentPadding = ButtonDefaults.ButtonWithIconContentPadding,
    ) {
        if (busy) {
            CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
        } else {
            Icon(icon, null)
        }
        Spacer(Modifier.width(ButtonDefaults.IconSpacing))
        Text(text, style = MaterialTheme.typography.titleMedium)
    }
}
