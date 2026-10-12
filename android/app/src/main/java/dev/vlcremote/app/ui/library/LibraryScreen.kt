package dev.vlcremote.app.ui.library

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.ui.common.commandErrorMessage
import dev.vlcremote.app.ui.common.displayNameFromUri
import dev.vlcremote.app.ui.common.formatSize
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.protocol.model.Entry
import org.koin.androidx.compose.koinViewModel

@Composable
fun LibraryScreen(onPlayed: () -> Unit, viewModel: LibraryViewModel = koinViewModel()) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val tick = rememberTick(true)

    BackHandler(enabled = !ui.isRoot) { viewModel.up() }

    Column(Modifier.fillMaxSize()) {
        // Toolbar: up / home / favorites / media filter / refresh
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = viewModel::up, enabled = !ui.isRoot) { Icon(Icons.Default.ArrowUpward, stringResource(R.string.up)) }
            IconButton(onClick = viewModel::goRoot) { Icon(Icons.Default.Home, stringResource(R.string.drives)) }
            IconButton(onClick = viewModel::toggleFavorite, enabled = !ui.isRoot) {
                Icon(if (ui.isFavorite) Icons.Default.Star else Icons.Default.StarBorder, stringResource(R.string.favorite))
            }
            Spacer(Modifier.weight(1f))
            FilterChip(
                selected = ui.mediaOnly,
                onClick = { viewModel.setMediaOnly(!ui.mediaOnly) },
                label = { Text(stringResource(R.string.media_only)) },
            )
            IconButton(onClick = viewModel::reload) { Icon(Icons.Default.Refresh, stringResource(R.string.refresh)) }
        }

        // Breadcrumbs
        Row(
            Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = viewModel::goRoot) { Text(stringResource(R.string.drives)) }
            ui.crumbs.forEach { crumb ->
                Text("›", color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton(onClick = { viewModel.open(crumb.uri) }) { Text(crumb.name, maxLines = 1) }
            }
        }

        // Favorites shortcuts on the root screen
        if (ui.isRoot && ui.favorites.isNotEmpty()) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 12.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                ui.favorites.forEach { fav ->
                    FilterChip(
                        selected = false,
                        onClick = { viewModel.open(fav) },
                        label = { Text(displayNameFromUri(fav), maxLines = 1) },
                        leadingIcon = { Icon(Icons.Default.Star, null) },
                    )
                }
            }
        }
        HorizontalDivider()

        Box(Modifier.fillMaxSize()) {
            when {
                !ui.online -> CenterText(stringResource(R.string.library_offline))
                ui.error != null -> Column(Modifier.align(Alignment.Center), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(ui.error!!.commandErrorMessage(context), color = MaterialTheme.colorScheme.error)
                    TextButton(onClick = viewModel::reload) { Text(stringResource(R.string.retry)) }
                }
                ui.loading && ui.entries.isEmpty() -> CircularProgressIndicator(Modifier.align(Alignment.Center))
                ui.visibleEntries.isEmpty() -> CenterText(stringResource(R.string.folder_empty))
                else -> LazyColumn(Modifier.fillMaxSize()) {
                    items(ui.visibleEntries, key = { it.uri }) { entry ->
                        EntryRow(
                            entry = entry,
                            onClick = {
                                tick()
                                if (entry.isDir) viewModel.open(entry.uri) else {
                                    viewModel.play(entry)
                                    onPlayed()
                                }
                            },
                            onLongClick = {
                                tick()
                                if (!entry.isDir) viewModel.showMenu(entry) else viewModel.open(entry.uri)
                            },
                        )
                    }
                }
            }
            if (ui.loading && ui.entries.isNotEmpty()) {
                CircularProgressIndicator(Modifier.align(Alignment.TopEnd).padding(8.dp).width(20.dp), strokeWidth = 2.dp)
            }
        }
    }

    ui.menuFor?.let { entry ->
        ModalBottomSheet(onDismissRequest = { viewModel.showMenu(null) }) {
            Text(entry.name, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp), maxLines = 2, overflow = TextOverflow.Ellipsis)
            ListItem(
                headlineContent = { Text(stringResource(R.string.action_play)) },
                leadingContent = { Icon(Icons.Default.PlayArrow, null) },
                modifier = Modifier.combinedClickable(onClick = { viewModel.play(entry); viewModel.showMenu(null); onPlayed() }),
            )
            ListItem(
                headlineContent = { Text(stringResource(R.string.action_enqueue)) },
                leadingContent = { Icon(Icons.AutoMirrored.Filled.PlaylistAdd, null) },
                modifier = Modifier.combinedClickable(onClick = { viewModel.enqueue(entry); viewModel.showMenu(null) }),
            )
            Spacer(Modifier.padding(16.dp))
        }
    }
}

@Composable
private fun CenterText(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(24.dp))
    }
}

@Composable
private fun EntryRow(entry: Entry, onClick: () -> Unit, onLongClick: () -> Unit) {
    ListItem(
        headlineContent = { Text(entry.name, maxLines = 2, overflow = TextOverflow.Ellipsis) },
        supportingContent = if (!entry.isDir && entry.size != null) {
            { Text(formatSize(entry.size)) }
        } else {
            null
        },
        leadingContent = {
            Icon(
                when {
                    entry.isDir -> Icons.Default.Folder
                    entry.isMediaFile -> Icons.Default.Movie
                    else -> Icons.AutoMirrored.Filled.InsertDriveFile
                },
                contentDescription = null,
                tint = if (entry.isDir) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        },
        modifier = Modifier.combinedClickable(onClick = onClick, onLongClick = onLongClick),
    )
}
