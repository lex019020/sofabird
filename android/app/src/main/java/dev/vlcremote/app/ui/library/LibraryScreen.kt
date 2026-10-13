package dev.vlcremote.app.ui.library

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyItemScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Home
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.ui.common.LabelText
import dev.vlcremote.app.ui.common.commandErrorMessage
import dev.vlcremote.app.ui.common.displayNameFromUri
import dev.vlcremote.app.ui.common.formatSize
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.app.ui.common.withNameBreaks
import dev.vlcremote.app.ui.history.ContinueWatchingRow
import dev.vlcremote.app.ui.history.EntryBadge
import dev.vlcremote.app.ui.history.HistoryMenuEntries
import dev.vlcremote.app.ui.history.WatchProgressBar
import dev.vlcremote.app.ui.history.WatchedMark
import dev.vlcremote.app.ui.history.displayTitle
import dev.vlcremote.app.ui.history.historyBadge
import dev.vlcremote.protocol.model.Entry
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import org.koin.androidx.compose.koinViewModel
import org.koin.compose.koinInject

@Composable
fun LibraryScreen(onPlayed: () -> Unit, onOpenHistory: () -> Unit, viewModel: LibraryViewModel = koinViewModel()) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val tick = rememberTick(true)

    // The PC reports a changed history: refresh "Continue watching" and the progress marks (only while shown).
    val controller: PlayerController = koinInject()
    val revisionFlow = remember(controller) { controller.state.map { it.history?.revision }.distinctUntilChanged() }
    val revision by revisionFlow.collectAsStateWithLifecycle(initialValue = controller.state.value.history?.revision)
    LaunchedEffect(revision) { viewModel.onHistoryRevision(revision) }

    BackHandler(enabled = !ui.isRoot) { viewModel.up() }

    // With a history the tab starts with "Continue watching" (or, with nothing to continue, a "History" link).
    val showContinue = ui.historyEnabled
    val listState = rememberLazyListState()
    // Another folder opens at its top; a "Continue watching" row scrolled away stays away.
    val headerIndex = if (showContinue) 1 else 0
    LaunchedEffect(ui.uri) {
        if (listState.firstVisibleItemIndex > headerIndex) listState.scrollToItem(headerIndex)
    }

    if (!ui.online) {
        Column(Modifier.fillMaxSize()) {
            FolderToolbar(ui, viewModel)
            HorizontalDivider()
            CenterText(stringResource(R.string.library_offline))
        }
        return
    }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
            // "Continue watching" on top of the tab; it scrolls away, the folder toolbar then sticks.
            if (showContinue && ui.continueCards.isEmpty()) {
                item(key = "continue") { HistoryLink(onOpenHistory) }
            } else if (showContinue) {
                item(key = "continue") {
                    ContinueWatchingRow(
                        cards = ui.continueCards,
                        onOpen = { card ->
                            tick()
                            viewModel.playHistory(card.item)
                            onPlayed()
                        },
                        onMenu = { card ->
                            tick()
                            viewModel.showContinueMenu(card.item)
                        },
                        onHistory = onOpenHistory,
                        modifier = Modifier.padding(bottom = 4.dp),
                    )
                }
            }
            stickyHeader(key = "toolbar") {
                Column(Modifier.background(MaterialTheme.colorScheme.background)) {
                    FolderToolbar(ui, viewModel)
                    HorizontalDivider()
                }
            }
            when {
                ui.error != null -> item(key = "error") {
                    StatusItem(fill = !showContinue) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(
                                ui.error!!.commandErrorMessage(context),
                                color = MaterialTheme.colorScheme.error,
                                textAlign = TextAlign.Center,
                            )
                            TextButton(onClick = viewModel::reload) { Text(stringResource(R.string.retry)) }
                        }
                    }
                }
                ui.loading && ui.entries.isEmpty() -> item(key = "loading") {
                    StatusItem(fill = !showContinue) { CircularProgressIndicator() }
                }
                ui.visibleEntries.isEmpty() -> item(key = "empty") {
                    StatusItem(fill = !showContinue) {
                        Text(stringResource(R.string.folder_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                else -> items(ui.visibleEntries, key = { it.uri }) { entry ->
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

    ui.menuFor?.let { entry ->
        ModalBottomSheet(onDismissRequest = { viewModel.showMenu(null) }) {
            Text(
                entry.name.withNameBreaks(),
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
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

    ui.continueMenuFor?.let { item ->
        ModalBottomSheet(onDismissRequest = { viewModel.showContinueMenu(null) }) {
            Text(
                item.displayTitle.withNameBreaks(),
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )
            HorizontalDivider()
            HistoryMenuEntries(
                finished = item.finished,
                onPlay = {
                    viewModel.showContinueMenu(null)
                    viewModel.playHistory(item)
                    onPlayed()
                },
                onSetFinished = { finished ->
                    tick()
                    viewModel.showContinueMenu(null)
                    // A card in the row is never finished, so this is always "mark as watched".
                    if (finished) viewModel.markWatched(item)
                },
                onRemove = {
                    tick()
                    viewModel.showContinueMenu(null)
                    viewModel.removeFromHistory(item)
                },
            )
            Spacer(Modifier.height(24.dp))
        }
    }
}

/** Up / home / favourite / media filter / refresh, then breadcrumbs and favourites. */
@Composable
private fun FolderToolbar(ui: LibraryUiState, viewModel: LibraryViewModel) {
    Column(Modifier.fillMaxWidth()) {
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
                label = { LabelText(stringResource(R.string.media_only)) },
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
            TextButton(onClick = viewModel::goRoot) { LabelText(stringResource(R.string.drives)) }
            ui.crumbs.forEach { crumb ->
                Text("›", color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton(onClick = { viewModel.open(crumb.uri) }) { LabelText(crumb.name) }
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
                        label = { LabelText(displayNameFromUri(fav)) },
                        leadingIcon = { Icon(Icons.Default.Star, null) },
                    )
                }
            }
        }
    }
}

/** Top of the tab when there is nothing to continue: the way to the full history. */
@Composable
private fun HistoryLink(onOpenHistory: () -> Unit) {
    ListItem(
        headlineContent = { Text(stringResource(R.string.history_title)) },
        leadingContent = { Icon(Icons.Default.History, null, tint = MaterialTheme.colorScheme.primary) },
        trailingContent = { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, null) },
        modifier = Modifier.combinedClickable(onClick = onOpenHistory),
    )
}

/** Error / loading / empty state inside the list: centered in the free space, or a block under the row. */
@Composable
private fun LazyItemScope.StatusItem(fill: Boolean, content: @Composable () -> Unit) {
    Box(
        (if (fill) Modifier.fillParentMaxSize() else Modifier.fillParentMaxWidth().padding(vertical = 48.dp))
            .padding(horizontal = 24.dp),
        contentAlignment = Alignment.Center,
    ) { content() }
}

@Composable
private fun CenterText(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(24.dp))
    }
}

@Composable
private fun EntryRow(entry: Entry, onClick: () -> Unit, onLongClick: () -> Unit) {
    val badge = entry.historyBadge()
    val size = if (!entry.isDir && entry.size != null) formatSize(LocalContext.current, entry.size) else ""
    ListItem(
        headlineContent = { Text(entry.name.withNameBreaks(), maxLines = 2, overflow = TextOverflow.Ellipsis) },
        supportingContent = if (size.isNotEmpty() || badge is EntryBadge.Started) {
            {
                Column {
                    if (size.isNotEmpty()) Text(size)
                    // Started: a thin bar of the watched part.
                    if (badge is EntryBadge.Started) {
                        WatchProgressBar(badge.fraction, Modifier.padding(top = 6.dp).fillMaxWidth(0.5f), height = 3.dp)
                    }
                }
            }
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
        trailingContent = if (badge == EntryBadge.Finished) {
            { WatchedMark() }
        } else {
            null
        },
        modifier = Modifier.combinedClickable(onClick = onClick, onLongClick = onLongClick),
    )
}
