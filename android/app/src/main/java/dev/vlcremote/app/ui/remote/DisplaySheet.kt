package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.background
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
import androidx.compose.material.icons.filled.Laptop
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.vlcremote.app.R
import dev.vlcremote.app.ui.common.commandErrorMessage
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.protocol.companion.Display

/**
 * "Display for VLC": monitors from `listDisplays`; a tap moves VLC there and remembers it
 * (`sys moveToDisplay`), "Do not manage" = `sys preferredDisplay {null}`. Opened from the "Display" chip
 * on the remote and from the "More" sheet.
 */
@Composable
fun DisplaySheet(ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tick = rememberTick(ui.settings.haptics)
    val choice = ui.displayChoice
    val busy = ui.displayPending != null
    val enabled = ui.connection.isOnline && !busy && choice.canChoose
    LaunchedEffect(Unit) { viewModel.loadDisplays() }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Column(
            Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp)
                .padding(bottom = 32.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    stringResource(R.string.sheet_display_title),
                    style = MaterialTheme.typography.titleLarge,
                    modifier = Modifier.weight(1f),
                )
                if (ui.displaysLoading) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                IconButton(onClick = { viewModel.loadDisplays() }, enabled = ui.connection.isOnline) {
                    Icon(Icons.Default.Refresh, stringResource(R.string.refresh))
                }
            }
            Text(
                stringResource(
                    when {
                        choice.supportsIds && choice.vlcOpen -> R.string.display_hint_open
                        choice.supportsIds -> R.string.display_hint_closed
                        choice.vlcOpen -> R.string.display_hint_legacy
                        else -> R.string.display_hint_legacy_closed
                    },
                ),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(12.dp))

            if (choice.items.isEmpty() && !ui.displaysLoading) {
                Text(stringResource(R.string.displays_none), color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            choice.items.forEach { display ->
                val key = display.key
                // Already where it should be: nothing to do (a tap on it would only re-send the same move).
                val noOp = key == choice.selectedKey && (!choice.vlcOpen || key == choice.currentKey)
                DisplayRow(
                    display = display,
                    selected = key == choice.selectedKey,
                    vlcHere = key == choice.currentKey,
                    opensHere = choice.supportsIds && !choice.vlcOpen && key == choice.preferredKey,
                    pending = ui.displayPending == key,
                    enabled = enabled,
                    onClick = {
                        if (!noOp) {
                            tick()
                            viewModel.moveVlcTo(display)
                        }
                    },
                )
            }
            if (choice.preferredMissing) {
                Text(
                    stringResource(R.string.display_preferred_missing),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 6.dp),
                )
            }
            if (choice.canUnmanage) {
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                OptionRow(
                    icon = Icons.Default.VisibilityOff,
                    iconDescription = null,
                    title = stringResource(R.string.display_unmanaged),
                    selected = choice.unmanagedSelected,
                    pending = ui.displayPending == RemoteUiState.UNMANAGED_KEY,
                    enabled = ui.connection.isOnline && !busy,
                    onClick = {
                        if (!choice.unmanagedSelected) {
                            tick()
                            viewModel.unmanageDisplay()
                        }
                    },
                ) {
                    Text(
                        stringResource(R.string.display_unmanaged_hint),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            ui.displayError?.let { error ->
                Text(
                    error.commandErrorMessage(LocalContext.current),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(top = 12.dp),
                )
            }
        }
    }
}

/** Monitor name: EDID friendly name, else "Display N". */
@Composable
fun displayLabel(display: Display): String =
    display.name?.takeIf { it.isNotBlank() } ?: stringResource(R.string.display_n, display.index + 1)

/** TV / external monitor vs the laptop panel; unknown kind (old companions) — a generic monitor. */
fun displayIcon(display: Display?): ImageVector = when {
    display == null -> Icons.Default.Tv
    display.isExternal -> Icons.Default.Tv
    display.isInternal -> Icons.Default.Laptop
    else -> Icons.Default.DesktopWindows
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun DisplayRow(
    display: Display,
    selected: Boolean,
    vlcHere: Boolean,
    opensHere: Boolean,
    pending: Boolean,
    enabled: Boolean,
    onClick: () -> Unit,
) {
    val kindDescription = when {
        display.isExternal -> stringResource(R.string.display_kind_external)
        display.isInternal -> stringResource(R.string.display_kind_internal)
        else -> null
    }
    OptionRow(
        icon = displayIcon(display),
        iconDescription = kindDescription,
        iconTint = if (vlcHere) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
        title = displayLabel(display),
        selected = selected,
        pending = pending,
        enabled = enabled,
        onClick = onClick,
    ) {
        FlowRow(
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
            modifier = Modifier.padding(top = 2.dp),
        ) {
            display.bounds?.takeIf { it.width > 0 && it.height > 0 }?.let { b ->
                Text(
                    stringResource(R.string.display_resolution, b.width, b.height),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.align(Alignment.CenterVertically),
                )
            }
            if (display.primary) {
                Badge(
                    stringResource(R.string.display_primary),
                    MaterialTheme.colorScheme.secondaryContainer,
                    MaterialTheme.colorScheme.onSecondaryContainer,
                )
            }
            if (vlcHere) {
                Badge(
                    stringResource(R.string.display_vlc_here),
                    MaterialTheme.colorScheme.primaryContainer,
                    MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
        }
        if (opensHere) {
            Text(
                stringResource(R.string.display_opens_here),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(top = 2.dp),
            )
        }
    }
}

/** Radio row: radio · icon · title + [details] · progress while the action is in flight. */
@Composable
private fun OptionRow(
    icon: ImageVector,
    iconDescription: String?,
    title: String,
    selected: Boolean,
    pending: Boolean,
    enabled: Boolean,
    onClick: () -> Unit,
    iconTint: Color = MaterialTheme.colorScheme.onSurfaceVariant,
    details: @Composable () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .selectable(selected = selected, enabled = enabled, role = Role.RadioButton, onClick = onClick)
            .padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = null, enabled = enabled)
        Spacer(Modifier.width(12.dp))
        Icon(icon, contentDescription = iconDescription, tint = iconTint, modifier = Modifier.size(28.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
            details()
        }
        Box(Modifier.size(32.dp), contentAlignment = Alignment.Center) {
            if (pending) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
        }
    }
}

@Composable
private fun Badge(text: String, container: Color, content: Color) {
    Text(
        text,
        style = MaterialTheme.typography.labelSmall,
        color = content,
        modifier = Modifier
            .background(container, RoundedCornerShape(6.dp))
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}
