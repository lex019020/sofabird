package dev.vlcremote.app.ui.history

import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.RemoveDone
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
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
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.ui.common.commandErrorMessage
import dev.vlcremote.app.ui.common.rememberConfirmHaptic
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.protocol.model.HistoryItem
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import org.koin.androidx.compose.koinViewModel
import org.koin.compose.koinInject

/** Full watch history: grouped by date, swipe to remove, menu to mark watched / unwatched, "Clear history". */
@Composable
fun HistoryScreen(onBack: () -> Unit, onPlayed: () -> Unit, viewModel: HistoryViewModel = koinViewModel()) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val controller: PlayerController = koinInject()
    val settings: SettingsRepository = koinInject()
    val prefs by settings.settings.collectAsStateWithLifecycle()
    val server by controller.activeServer.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val tick = rememberTick(prefs.haptics)
    val confirm = rememberConfirmHaptic(prefs.haptics)

    // Refresh when the PC reports a changed history (only while this screen is shown).
    val revisionFlow = remember(controller) { controller.state.map { it.history?.revision }.distinctUntilChanged() }
    val revision by revisionFlow.collectAsStateWithLifecycle(initialValue = controller.state.value.history?.revision)
    LaunchedEffect(revision) { viewModel.onRevision(revision) }

    var overflow by remember { mutableStateOf(false) }
    var confirmClear by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.history_title)) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back)) } },
                actions = {
                    if (ui.available && ui.online && ui.items.isNotEmpty()) {
                        Box {
                            IconButton(onClick = { overflow = true }) { Icon(Icons.Default.MoreVert, stringResource(R.string.more_actions)) }
                            DropdownMenu(expanded = overflow, onDismissRequest = { overflow = false }) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.history_clear)) },
                                    leadingIcon = { Icon(Icons.Default.Delete, null) },
                                    onClick = {
                                        overflow = false
                                        confirmClear = true
                                    },
                                )
                            }
                        }
                    }
                },
            )
        },
    ) { padding ->
        Box(
            Modifier
                .fillMaxSize()
                .padding(padding),
        ) {
            when {
                !ui.available -> CenterMessage(stringResource(R.string.history_unavailable))
                !ui.online && ui.items.isEmpty() -> CenterMessage(stringResource(R.string.err_not_connected))
                ui.error != null && ui.items.isEmpty() -> Column(
                    Modifier.align(Alignment.Center).padding(24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(ui.error!!.commandErrorMessage(context), color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center)
                    TextButton(onClick = viewModel::reload) { Text(stringResource(R.string.retry)) }
                }
                ui.loading && ui.items.isEmpty() -> CircularProgressIndicator(Modifier.align(Alignment.Center))
                ui.items.isEmpty() -> CenterMessage(stringResource(R.string.history_empty))
                else -> LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
                    ui.groups.forEach { group ->
                        stickyHeader(key = "header-${group.group}") { GroupHeader(group.group) }
                        items(group.items, key = { it.uri }) { item ->
                            SwipeToRemove(
                                onRemove = {
                                    confirm()
                                    viewModel.remove(item)
                                },
                            ) {
                                HistoryRow(
                                    item = item,
                                    onClick = {
                                        tick()
                                        viewModel.play(item)
                                        onPlayed()
                                    },
                                    onMenu = {
                                        tick()
                                        viewModel.showMenu(item)
                                    },
                                )
                            }
                        }
                    }
                    if (ui.loadingMore) {
                        item(key = "more") {
                            Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                                CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                            }
                        }
                    }
                }
            }
            if (ui.loading && ui.items.isNotEmpty()) {
                CircularProgressIndicator(Modifier.align(Alignment.TopEnd).padding(8.dp).width(20.dp), strokeWidth = 2.dp)
            }
        }
    }

    // Paging: the next page when the end of the list comes into view.
    val canLoadMore by rememberUpdatedState(ui.canLoadMore)
    LaunchedEffect(listState) {
        snapshotFlow {
            val info = listState.layoutInfo
            val last = info.visibleItemsInfo.lastOrNull()?.index ?: -1
            info.totalItemsCount > 0 && last >= info.totalItemsCount - LOAD_MORE_THRESHOLD
        }
            .distinctUntilChanged()
            .collect { nearEnd -> if (nearEnd && canLoadMore) viewModel.loadMore() }
    }
    LaunchedEffect(ui.items.size, ui.total) {
        val info = listState.layoutInfo
        val last = info.visibleItemsInfo.lastOrNull()?.index ?: -1
        if (ui.canLoadMore && info.totalItemsCount > 0 && last >= info.totalItemsCount - LOAD_MORE_THRESHOLD) viewModel.loadMore()
    }

    ui.menuFor?.let { item ->
        HistoryItemMenu(
            item = item,
            onDismiss = { viewModel.showMenu(null) },
            onPlay = {
                viewModel.showMenu(null)
                viewModel.play(item)
                onPlayed()
            },
            onSetFinished = { finished ->
                confirm()
                viewModel.showMenu(null)
                viewModel.setFinished(item, finished)
            },
            onRemove = {
                confirm()
                viewModel.showMenu(null)
                viewModel.remove(item)
            },
        )
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text(stringResource(R.string.history_clear)) },
            text = { Text(stringResource(R.string.history_clear_confirm, server?.displayName.orEmpty())) },
            confirmButton = {
                TextButton(onClick = {
                    confirm()
                    confirmClear = false
                    viewModel.clearAll()
                }) { Text(stringResource(R.string.history_clear_action), color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

private const val LOAD_MORE_THRESHOLD = 5

@Composable
private fun CenterMessage(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(
            text,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(32.dp),
        )
    }
}

@Composable
private fun GroupHeader(group: DateGroup) {
    Text(
        stringResource(
            when (group) {
                DateGroup.TODAY -> R.string.history_today
                DateGroup.YESTERDAY -> R.string.history_yesterday
                DateGroup.THIS_WEEK -> R.string.history_this_week
                DateGroup.EARLIER -> R.string.history_earlier
            },
        ),
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface)
            .padding(horizontal = 16.dp, vertical = 8.dp),
    )
}

@Composable
private fun SwipeToRemove(onRemove: () -> Unit, content: @Composable () -> Unit) {
    val latest by rememberUpdatedState(onRemove)
    val state = rememberSwipeToDismissBoxState(
        confirmValueChange = { value ->
            if (value == SwipeToDismissBoxValue.EndToStart) {
                latest()
                true
            } else {
                false
            }
        },
    )
    SwipeToDismissBox(
        state = state,
        enableDismissFromStartToEnd = false,
        backgroundContent = {
            Row(
                Modifier
                    .fillMaxSize()
                    .background(MaterialTheme.colorScheme.errorContainer)
                    .padding(horizontal = 24.dp),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    stringResource(R.string.remove),
                    color = MaterialTheme.colorScheme.onErrorContainer,
                    style = MaterialTheme.typography.labelLarge,
                )
                Spacer(Modifier.width(8.dp))
                Icon(Icons.Default.Delete, contentDescription = null, tint = MaterialTheme.colorScheme.onErrorContainer)
            }
        },
    ) { content() }
}

@Composable
private fun HistoryRow(item: HistoryItem, onClick: () -> Unit, onMenu: () -> Unit) {
    ListItem(
        headlineContent = { Text(item.displayTitle, maxLines = 2, overflow = TextOverflow.Ellipsis) },
        supportingContent = {
            Column {
                item.folderName?.let { folder ->
                    Text(folder, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                when {
                    item.finished -> Row(Modifier.padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            Icons.Default.CheckCircle,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.size(16.dp),
                        )
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.history_watched), style = MaterialTheme.typography.labelMedium)
                    }
                    item.isStarted -> Column(Modifier.padding(top = 6.dp)) {
                        WatchProgressBar(item.fraction.toFloat(), Modifier.fillMaxWidth(0.6f))
                        Spacer(Modifier.height(4.dp))
                        Text(positionOfLength(item.position, item.length), style = MaterialTheme.typography.labelMedium)
                    }
                }
            }
        },
        leadingContent = {
            Icon(Icons.Default.Movie, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        },
        trailingContent = {
            IconButton(onClick = onMenu) { Icon(Icons.Default.MoreVert, stringResource(R.string.more_actions)) }
        },
        modifier = Modifier.combinedClickable(onClick = onClick, onLongClick = onMenu),
    )
}

@Composable
private fun HistoryItemMenu(
    item: HistoryItem,
    onDismiss: () -> Unit,
    onPlay: () -> Unit,
    onSetFinished: (Boolean) -> Unit,
    onRemove: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Text(
            item.displayTitle,
            style = MaterialTheme.typography.titleMedium,
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
        )
        HorizontalDivider()
        HistoryMenuEntries(
            finished = item.finished,
            onPlay = onPlay,
            onSetFinished = onSetFinished,
            onRemove = onRemove,
        )
        Spacer(Modifier.height(24.dp))
    }
}

/** Menu entries shared by the History screen and the "Continue watching" cards. */
@Composable
fun HistoryMenuEntries(
    finished: Boolean,
    onPlay: () -> Unit,
    onSetFinished: (Boolean) -> Unit,
    onRemove: () -> Unit,
) {
    ListItem(
        headlineContent = { Text(stringResource(R.string.action_play)) },
        leadingContent = { Icon(Icons.Default.PlayArrow, null) },
        modifier = Modifier.combinedClickable(onClick = onPlay),
    )
    ListItem(
        headlineContent = { Text(stringResource(if (finished) R.string.history_mark_unwatched else R.string.history_mark_watched)) },
        leadingContent = { Icon(if (finished) Icons.Default.RemoveDone else Icons.Default.CheckCircle, null) },
        modifier = Modifier.combinedClickable(onClick = { onSetFinished(!finished) }),
    )
    ListItem(
        headlineContent = { Text(stringResource(R.string.history_remove)) },
        leadingContent = { Icon(Icons.Default.Delete, null) },
        modifier = Modifier.combinedClickable(onClick = onRemove),
    )
}
