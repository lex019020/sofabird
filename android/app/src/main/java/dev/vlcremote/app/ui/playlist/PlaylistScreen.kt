package dev.vlcremote.app.ui.playlist

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.compositeOver
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.ui.common.commandErrorMessage
import dev.vlcremote.app.ui.common.formatTime
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.app.ui.common.withNameBreaks
import dev.vlcremote.protocol.model.PlaylistItem
import org.koin.androidx.compose.koinViewModel

@Composable
fun PlaylistScreen(viewModel: PlaylistViewModel = koinViewModel()) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val haptics by viewModel.haptics.collectAsStateWithLifecycle()
    val tick = rememberTick(haptics)
    var confirmClear by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                stringResource(R.string.playlist_count, ui.items.size),
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.weight(1f),
            )
            if (ui.loading) CircularProgressIndicator(Modifier.width(18.dp), strokeWidth = 2.dp)
            IconButton(onClick = viewModel::refresh, enabled = ui.online) { Icon(Icons.Default.Refresh, stringResource(R.string.refresh)) }
            IconButton(onClick = { confirmClear = true }, enabled = ui.online && ui.items.isNotEmpty()) {
                Icon(Icons.Default.DeleteSweep, stringResource(R.string.playlist_clear))
            }
        }
        HorizontalDivider()
        Box(Modifier.fillMaxSize()) {
            when {
                !ui.online -> Center(stringResource(R.string.library_offline))
                ui.error != null && ui.items.isEmpty() -> Center(ui.error!!.commandErrorMessage(context))
                ui.items.isEmpty() && !ui.loading -> Center(stringResource(R.string.playlist_empty))
                else -> LazyColumn(Modifier.fillMaxSize()) {
                    items(ui.items, key = { it.plId }) { item ->
                        DismissibleRow(onRemove = { tick(); viewModel.remove(item) }) {
                            PlaylistRow(item, onClick = { tick(); viewModel.play(item) })
                        }
                    }
                }
            }
        }
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text(stringResource(R.string.playlist_clear)) },
            text = { Text(stringResource(R.string.playlist_clear_confirm)) },
            confirmButton = {
                TextButton(onClick = { confirmClear = false; viewModel.clear() }) { Text(stringResource(R.string.playlist_clear)) }
            },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
private fun Center(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(24.dp))
    }
}

@Composable
private fun DismissibleRow(onRemove: () -> Unit, content: @Composable () -> Unit) {
    val state = rememberSwipeToDismissBoxState(
        confirmValueChange = { value ->
            if (value == SwipeToDismissBoxValue.EndToStart || value == SwipeToDismissBoxValue.StartToEnd) {
                onRemove()
                true
            } else {
                false
            }
        },
    )
    SwipeToDismissBox(
        state = state,
        backgroundContent = {
            Box(
                Modifier
                    .fillMaxSize()
                    .background(MaterialTheme.colorScheme.errorContainer)
                    .padding(horizontal = 24.dp),
                contentAlignment = Alignment.CenterEnd,
            ) {
                Icon(Icons.Default.Delete, stringResource(R.string.remove), tint = MaterialTheme.colorScheme.onErrorContainer)
            }
        },
    ) { content() }
}

@Composable
private fun PlaylistRow(item: PlaylistItem, onClick: () -> Unit) {
    ListItem(
        headlineContent = {
            Text(
                item.name.withNameBreaks(),
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                fontWeight = if (item.current) FontWeight.Bold else FontWeight.Normal,
                color = if (item.current) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
            )
        },
        supportingContent = if (item.durationSec > 0) {
            { Text(formatTime(item.durationSec)) }
        } else {
            null
        },
        leadingContent = {
            if (item.current) {
                Icon(Icons.Default.PlayArrow, null, tint = MaterialTheme.colorScheme.primary)
            } else {
                Spacer(Modifier.width(24.dp))
            }
        },
        // Opaque: the swipe-to-remove background (red, with a bin icon) lies right underneath the row.
        colors = ListItemDefaults.colors(
            containerColor = if (item.current) {
                MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.25f).compositeOver(MaterialTheme.colorScheme.surface)
            } else {
                MaterialTheme.colorScheme.surface
            },
        ),
        modifier = Modifier.clickable(onClick = onClick),
    )
}
