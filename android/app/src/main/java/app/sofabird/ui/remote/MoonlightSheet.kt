package app.sofabird.ui.remote

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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SportsEsports
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.MoonlightHost
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.commandErrorMessage
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.withNameBreaks

/**
 * "Another PC's screen" (capability `moonlight`), from the home screen's "Another PC" tile and from "More": the
 * computers paired in Moonlight on the TV PC as chips (when there are several), the chosen one's apps (Desktop first),
 * "Refresh" (each computer is asked for its apps) and "Show on the TV" (`moonlightStart {host, app}`, remembered for
 * next time). While a stream runs: what is on the TV and "End streaming".
 */
@Composable
fun MoonlightSheet(ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tick = rememberTick(ui.settings.haptics)
    val chooser = viewModel.moonlight
    LaunchedEffect(Unit) { chooser.open() }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        MoonlightContent(
            ui = ui.moonlight,
            streamingHost = ui.streamingHost,
            online = ui.connection.isOnline,
            onPick = { host, app ->
                tick()
                chooser.pick(host, app)
            },
            onRefresh = chooser::refresh,
            onStart = { host, app ->
                tick()
                chooser.start(host, app)
                onDismiss()
            },
            onStop = {
                tick()
                viewModel.sendSystem(SystemCommand.MoonlightStop)
                onDismiss()
            },
            modifier = Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp)
                .padding(bottom = 32.dp),
        )
    }
}

/** The sheet without its ViewModel (screenshot tests): [streamingHost] — a stream is on the TV (`""` — from an unknown computer). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun MoonlightContent(
    ui: MoonlightUi,
    streamingHost: String?,
    online: Boolean,
    onPick: (host: String, app: String?) -> Unit,
    onRefresh: () -> Unit,
    onStart: (host: String, app: String) -> Unit,
    onStop: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.section_moonlight), style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            if (streamingHost == null) {
                if (ui.loading) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                IconButton(onClick = onRefresh, enabled = online && !ui.refreshing) {
                    Icon(Icons.Default.Refresh, stringResource(R.string.moonlight_refresh))
                }
            }
        }
        Text(
            stringResource(R.string.moonlight_sheet_hint),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Spacer(Modifier.height(12.dp))
        if (streamingHost != null) {
            StreamOnTv(streamingHost, online, onStop)
        } else {
            if (ui.refreshing) Hint(stringResource(R.string.moonlight_refreshing), MaterialTheme.colorScheme.primary)
            val host = ui.host
            when {
                host != null -> HostChoice(ui, host, online, onPick, onStart)
                ui.loaded -> Hint(stringResource(R.string.moonlight_no_host))
            }
        }
        ui.error?.let { error ->
            Text(
                error.commandErrorMessage(LocalContext.current),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error,
                modifier = Modifier.padding(top = 12.dp),
            )
        }
    }
}

/** The computers (chips, when there are several), the chosen one's apps and "Show on the TV". */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun HostChoice(
    ui: MoonlightUi,
    host: MoonlightHost,
    online: Boolean,
    onPick: (host: String, app: String?) -> Unit,
    onStart: (host: String, app: String) -> Unit,
) {
    if (ui.hosts.size > 1) {
        Label(stringResource(R.string.moonlight_computer))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            ui.hosts.forEach { h ->
                FilterChip(
                    selected = h.name == host.name,
                    onClick = { onPick(h.name, null) },
                    label = { LabelText(h.name) },
                    enabled = online,
                )
            }
        }
        Spacer(Modifier.height(8.dp))
    }
    if (host.stale) Hint(stringResource(R.string.moonlight_stale, host.name))
    Label(stringResource(R.string.moonlight_apps))
    if (host.apps.isEmpty()) Hint(stringResource(R.string.moonlight_apps_unknown, host.name))
    host.choices.forEach { app ->
        AppRow(app, selected = app == ui.app, enabled = online) { onPick(host.name, app) }
    }
    val app = ui.app
    Button(
        onClick = { if (app != null) onStart(host.name, app) },
        enabled = online && app != null,
        modifier = Modifier.padding(top = 12.dp),
    ) {
        Icon(Icons.Default.Tv, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        LabelText(stringResource(R.string.action_moonlight_start))
    }
}

/** One app as a radio row: Desktop (the whole screen) or a game / launcher. */
@Composable
private fun AppRow(app: String, selected: Boolean, enabled: Boolean, onClick: () -> Unit) {
    val desktop = app.equals(MoonlightHost.DESKTOP, ignoreCase = true)
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .selectable(selected = selected, enabled = enabled, role = Role.RadioButton, onClick = onClick)
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = null, enabled = enabled)
        Spacer(Modifier.width(12.dp))
        Icon(
            if (desktop) Icons.Default.DesktopWindows else Icons.Default.SportsEsports,
            contentDescription = null,
            tint = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(24.dp),
        )
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(app.withNameBreaks(), style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (desktop) {
                Text(
                    stringResource(R.string.moonlight_desktop_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** A stream is on the TV: from which computer, and the way back to the movie. */
@Composable
private fun StreamOnTv(host: String, online: Boolean, onStop: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Tv, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.moonlight_streaming_title), style = MaterialTheme.typography.titleMedium)
            }
            if (host.isNotEmpty()) {
                Text(
                    stringResource(R.string.moonlight_from, host),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 6.dp),
                )
            }
            Box(Modifier.padding(top = 12.dp)) {
                Button(onClick = onStop, enabled = online) { LabelText(stringResource(R.string.action_moonlight_stop)) }
            }
        }
    }
}

@Composable
private fun Label(text: String) {
    Text(text, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 4.dp, bottom = 6.dp))
}

@Composable
private fun Hint(text: String, color: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    Text(text, style = MaterialTheme.typography.bodySmall, color = color, modifier = Modifier.padding(bottom = 8.dp))
}
