package app.sofabird.ui.library

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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sofabird.R
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.commandErrorMessage
import app.sofabird.ui.common.displayNameFromUri
import app.sofabird.ui.common.formatSize
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.withNameBreaks
import app.sofabird.ui.history.ContinueWatchingRow
import app.sofabird.ui.history.EntryBadge
import app.sofabird.ui.history.HistoryMenuEntries
import app.sofabird.ui.history.WatchProgressBar
import app.sofabird.ui.history.WatchedMark
import app.sofabird.ui.history.displayTitle
import app.sofabird.ui.history.historyBadge
import app.sofabird.ui.media.LibraryViewSwitch
import app.sofabird.ui.media.MediaContent
import app.sofabird.ui.media.MediaViewModel
import org.koin.androidx.compose.koinViewModel

/** What the library screen can do; no-op defaults for previews and screenshot tests. */
interface LibraryActions {
    fun open(entryUri: String) {}
    fun up() {}
    fun goRoot() {}
    fun reload() {}
    fun setMediaOnly(value: Boolean) {}
    fun toggleFavorite() {}
    fun play(entry: Entry) {}
    fun enqueue(entry: Entry) {}
    fun showMenu(entry: Entry?) {}
    fun playHistory(item: HistoryItem) {}
    fun showContinueMenu(item: HistoryItem?) {}
    fun markWatched(item: HistoryItem) {}
    fun removeFromHistory(item: HistoryItem) {}
}

/**
 * The Library tab: with a companion library, the posters ([MediaContent]) or the folders ([LibraryContent]), chosen
 * with a switch on top; "Continue watching" heads both.
 */
@Composable
fun LibraryScreen(
    onPlayed: () -> Unit,
    onOpenHistory: () -> Unit,
    onOpenShow: (String) -> Unit,
    viewModel: LibraryViewModel = koinViewModel(),
    media: MediaViewModel = koinViewModel(),
) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val mediaUi by media.uiState.collectAsStateWithLifecycle()
    // The PC reports a changed history: refresh "Continue watching" and the progress marks (only while shown).
    val revision by viewModel.historyRevision.collectAsStateWithLifecycle(initialValue = null)
    LaunchedEffect(revision) { viewModel.onHistoryRevision(revision) }
    val viewSwitch: @Composable () -> Unit = {
        if (mediaUi.available) LibraryViewSwitch(mediaUi.postersView, media::setPostersView)
    }
    if (mediaUi.available && mediaUi.postersView) {
        val tick = rememberTick(ui.haptics)
        MediaContent(mediaUi, media, onOpenShow = { onOpenShow(it.key) }, onPlayed = onPlayed) {
            viewSwitch()
            ContinueSection(ui, viewModel, tick, onPlayed, onOpenHistory)
        }
        ContinueMenu(ui, viewModel, tick, onPlayed)
    } else {
        LibraryContent(ui, viewModel, onPlayed, onOpenHistory, header = viewSwitch)
    }
}

/** "Continue watching" (or, with nothing to continue, a "History" link) when the companion keeps a history. */
@Composable
private fun ContinueSection(
    ui: LibraryUiState,
    actions: LibraryActions,
    tick: () -> Unit,
    onPlayed: () -> Unit,
    onOpenHistory: () -> Unit,
) {
    if (!ui.historyEnabled) return
    if (ui.continueCards.isEmpty()) {
        HistoryLink(onOpenHistory)
        return
    }
    ContinueWatchingRow(
        cards = ui.continueCards,
        onOpen = { card ->
            tick()
            actions.playHistory(card.item)
            onPlayed()
        },
        onMenu = { card ->
            tick()
            actions.showContinueMenu(card.item)
        },
        onHistory = onOpenHistory,
        modifier = Modifier.padding(bottom = 4.dp),
    )
}

@Composable
fun LibraryContent(
    ui: LibraryUiState,
    actions: LibraryActions,
    onPlayed: () -> Unit,
    onOpenHistory: () -> Unit,
    header: @Composable () -> Unit = {},
) {
    val context = LocalContext.current
    val tick = rememberTick(ui.haptics)

    BackHandler(enabled = !ui.isRoot) { actions.up() }

    // With a history the tab starts with "Continue watching" (or, with nothing to continue, a "History" link).
    val showContinue = ui.historyEnabled
    val listState = rememberLazyListState()
    // Another folder opens at its top (the toolbar, item 1); a header scrolled away stays away.
    LaunchedEffect(ui.uri) {
        if (listState.firstVisibleItemIndex > 1) listState.scrollToItem(1)
    }

    if (!ui.online) {
        Column(Modifier.fillMaxSize()) {
            FolderToolbar(ui, actions)
            HorizontalDivider()
            CenterText(stringResource(R.string.library_offline))
        }
        return
    }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
            // The view switch and "Continue watching" on top of the tab; they scroll away, the folder toolbar then sticks.
            item(key = "continue") {
                Column {
                    header()
                    ContinueSection(ui, actions, tick, onPlayed, onOpenHistory)
                }
            }
            stickyHeader(key = "toolbar") {
                Column(Modifier.background(MaterialTheme.colorScheme.background)) {
                    FolderToolbar(ui, actions)
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
                            TextButton(onClick = actions::reload) { Text(stringResource(R.string.retry)) }
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
                            if (entry.isDir) actions.open(entry.uri) else {
                                actions.play(entry)
                                onPlayed()
                            }
                        },
                        onLongClick = {
                            tick()
                            if (!entry.isDir) actions.showMenu(entry) else actions.open(entry.uri)
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
        ModalBottomSheet(onDismissRequest = { actions.showMenu(null) }) {
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
                modifier = Modifier.combinedClickable(onClick = { actions.play(entry); actions.showMenu(null); onPlayed() }),
            )
            ListItem(
                headlineContent = { Text(stringResource(R.string.action_enqueue)) },
                leadingContent = { Icon(Icons.AutoMirrored.Filled.PlaylistAdd, null) },
                modifier = Modifier.combinedClickable(onClick = { actions.enqueue(entry); actions.showMenu(null) }),
            )
            Spacer(Modifier.padding(16.dp))
        }
    }

    ContinueMenu(ui, actions, tick, onPlayed)
}

@Composable
private fun ContinueMenu(ui: LibraryUiState, actions: LibraryActions, tick: () -> Unit, onPlayed: () -> Unit) {
    ui.continueMenuFor?.let { item ->
        ModalBottomSheet(onDismissRequest = { actions.showContinueMenu(null) }) {
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
                    actions.showContinueMenu(null)
                    actions.playHistory(item)
                    onPlayed()
                },
                onSetFinished = { finished ->
                    tick()
                    actions.showContinueMenu(null)
                    // A card in the row is never finished, so this is always "mark as watched".
                    if (finished) actions.markWatched(item)
                },
                onRemove = {
                    tick()
                    actions.showContinueMenu(null)
                    actions.removeFromHistory(item)
                },
            )
            Spacer(Modifier.height(24.dp))
        }
    }
}

/** Up / home / favourite / media filter / refresh, then breadcrumbs and favourites. */
@Composable
private fun FolderToolbar(ui: LibraryUiState, actions: LibraryActions) {
    Column(Modifier.fillMaxWidth()) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = actions::up, enabled = !ui.isRoot) { Icon(Icons.Default.ArrowUpward, stringResource(R.string.up)) }
            IconButton(onClick = actions::goRoot) { Icon(Icons.Default.Home, stringResource(R.string.drives)) }
            IconButton(onClick = actions::toggleFavorite, enabled = !ui.isRoot) {
                Icon(if (ui.isFavorite) Icons.Default.Star else Icons.Default.StarBorder, stringResource(R.string.favorite))
            }
            Spacer(Modifier.weight(1f))
            FilterChip(
                selected = ui.mediaOnly,
                onClick = { actions.setMediaOnly(!ui.mediaOnly) },
                label = { LabelText(stringResource(R.string.media_only)) },
            )
            IconButton(onClick = actions::reload) { Icon(Icons.Default.Refresh, stringResource(R.string.refresh)) }
        }

        // Breadcrumbs
        Row(
            Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = actions::goRoot) { LabelText(stringResource(R.string.drives)) }
            ui.crumbs.forEach { crumb ->
                Text("›", color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton(onClick = { actions.open(crumb.uri) }) { LabelText(crumb.name) }
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
                        onClick = { actions.open(fav) },
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
