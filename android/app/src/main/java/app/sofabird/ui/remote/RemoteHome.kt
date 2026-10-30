package app.sofabird.ui.remote

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.SmartDisplay
import androidx.compose.material.icons.filled.VideoLibrary
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.layout.Placeable
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import app.sofabird.R
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.HistoryChange
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.withNameBreaks
import app.sofabird.ui.history.HistoryMenuEntries
import app.sofabird.ui.history.NextEpisodeCard
import app.sofabird.ui.history.VideoThumbnail
import app.sofabird.ui.history.WatchProgressBar
import app.sofabird.ui.history.displayTitle
import app.sofabird.ui.history.isStarted
import app.sofabird.ui.history.positionOfLength

/**
 * Nothing on the TV ([RemoteUiState.home]): instead of an empty cover and idle buttons the remote offers what to
 * watch — the next episode, "Continue watching" (files and YouTube videos, as many as fit: the remote never scrolls)
 * — and where to find something: the Library, YouTube, another PC's screen (Moonlight). Starting VLC by itself is
 * left to "More".
 */
@Composable
internal fun RemoteHome(ui: RemoteUiState, next: NextEpisode?, actions: RemoteActions, landscape: Boolean) {
    val tick = rememberTick(ui.settings.haptics)
    val items = ui.continueItems.filter { it.uri != next?.uri }
    val play: (HistoryItem) -> Unit = { tick(); actions.send(PlayerCommand.Open(it.uri)) }
    var menuFor by remember { mutableStateOf<HistoryItem?>(null) }
    val list: @Composable (Modifier) -> Unit = { modifier ->
        ContinueList(items, ui.connection.isOnline, play, { menuFor = it }, { actions.open(RemotePlace.HISTORY) }, modifier)
    }
    if (landscape) {
        Row(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
            Column(Modifier.weight(0.56f).fillMaxHeight()) {
                Spacer(Modifier.height(8.dp))
                HomeTitle()
                next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = actions::playNext, onDismiss = actions::dismissNext) }
                list(Modifier.weight(1f))
            }
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(0.44f).fillMaxHeight(), verticalArrangement = Arrangement.Center) {
                HomeTiles(ui, actions)
                HomeQuickActions(ui, actions)
            }
        }
    } else {
        Column(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
            Spacer(Modifier.height(8.dp))
            HomeTitle()
            next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = actions::playNext, onDismiss = actions::dismissNext) }
            list(Modifier.weight(1f))
            Spacer(Modifier.height(8.dp))
            HomeTiles(ui, actions)
            HomeQuickActions(ui, actions)
            Spacer(Modifier.height(12.dp))
        }
    }
    menuFor?.let { item ->
        ContinueMenu(item, onDismiss = { menuFor = null }, onPlay = { play(item) }) { change ->
            tick()
            actions.changeHistory(item, change)
        }
    }
}

/** Long press on an entry: play, mark as watched, remove from the history (as in the Library). */
@Composable
private fun ContinueMenu(item: HistoryItem, onDismiss: () -> Unit, onPlay: () -> Unit, onChange: (HistoryChange) -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Text(
            item.displayTitle.withNameBreaks(),
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
        )
        HorizontalDivider()
        HistoryMenuEntries(
            finished = false, // "Continue watching" lists unfinished entries only
            onPlay = {
                onDismiss()
                onPlay()
            },
            onSetFinished = { finished ->
                onDismiss()
                onChange(HistoryChange.SetFinished(finished))
            },
            onRemove = {
                onDismiss()
                onChange(HistoryChange.Remove)
            },
        )
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun HomeTitle() {
    Text(
        stringResource(R.string.home_title),
        style = MaterialTheme.typography.headlineSmall,
        maxLines = 2,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.padding(bottom = 8.dp),
    )
}

/** "Continue watching" with a link to the history (tap — play, long press — menu); without any, a hint where to start. */
@Composable
private fun ContinueList(
    items: List<HistoryItem>,
    enabled: Boolean,
    onOpen: (HistoryItem) -> Unit,
    onMenu: (HistoryItem) -> Unit,
    onHistory: () -> Unit,
    modifier: Modifier,
) {
    if (items.isEmpty()) {
        Box(modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            Text(
                stringResource(R.string.home_empty_hint),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )
        }
        return
    }
    FittingColumn(spacing = 8.dp, modifier = modifier.fillMaxWidth()) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                stringResource(R.string.continue_watching),
                style = MaterialTheme.typography.titleSmall,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = onHistory) {
                Icon(Icons.Default.History, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                LabelText(stringResource(R.string.history_title))
            }
        }
        items.forEach { ContinueRow(it, enabled, onClick = { onOpen(it) }, onLongClick = { onMenu(it) }) }
    }
}

/** One entry: its picture (a YouTube thumbnail, a TMDB still or backdrop), title, where it stopped. */
@Composable
private fun ContinueRow(item: HistoryItem, enabled: Boolean, onClick: () -> Unit, onLongClick: () -> Unit) {
    // The picture does not grow with the font: at large sizes the title and the time need the width.
    val thumbWidth = (104f / LocalDensity.current.fontScale.coerceIn(1f, 1.3f)).dp
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            Modifier
                .combinedClickable(enabled = enabled, onClick = onClick, onLongClick = onLongClick)
                .padding(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            VideoThumbnail(
                item.artUrl,
                Modifier.width(thumbWidth),
                placeholder = if (item.isYouTube) Icons.Default.SmartDisplay else Icons.Default.Movie,
            )
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    item.displayTitle.withNameBreaks(),
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Row(Modifier.padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (item.isYouTube) {
                        Icon(
                            Icons.Default.SmartDisplay,
                            contentDescription = stringResource(R.string.history_youtube),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(14.dp).padding(end = 2.dp),
                        )
                    }
                    Text(
                        if (item.next && item.position <= 0) stringResource(R.string.history_next_badge)
                        else positionOfLength(item.position, item.length),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                if (item.isStarted) WatchProgressBar(item.fraction.toFloat(), Modifier.fillMaxWidth().padding(top = 6.dp))
            }
        }
    }
}

/** Where to find something to watch: the Library, YouTube on the TV, another PC's screen. */
@Composable
private fun HomeTiles(ui: RemoteUiState, actions: RemoteActions) {
    val online = ui.connection.isOnline
    val tiles = buildList {
        add(HomeTile(Icons.Default.VideoLibrary, stringResource(R.string.tab_library), stringResource(R.string.home_library_hint)) {
            actions.open(RemotePlace.LIBRARY)
        })
        if (ui.capabilities.youtube) {
            val hint = stringResource(R.string.home_youtube_hint)
            add(HomeTile(Icons.Default.SmartDisplay, stringResource(R.string.home_youtube), hint, online) {
                actions.open(RemotePlace.YOUTUBE)
            })
        }
        // Another computer through Moonlight: the sheet to choose it and its app; the hint names the last app shown.
        val host = ui.state.system?.moonlightHost
        if (ui.capabilities.moonlight && host != null) {
            add(HomeTile(Icons.Default.Computer, stringResource(R.string.home_stream), ui.moonlight.last?.app ?: host, online) {
                actions.open(RemotePlace.MOONLIGHT)
            })
        }
    }
    val tick = rememberTick(ui.settings.haptics)
    Row(
        Modifier
            .fillMaxWidth()
            .height(IntrinsicSize.Min),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        tiles.forEach { tile ->
            Card(
                onClick = { tick(); tile.onClick() },
                enabled = tile.enabled,
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
                modifier = Modifier.weight(1f).fillMaxHeight(),
            ) {
                Column(
                    Modifier
                        .fillMaxSize()
                        .padding(horizontal = 6.dp, vertical = 12.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Icon(tile.icon, contentDescription = null, modifier = Modifier.size(28.dp))
                    Spacer(Modifier.height(4.dp))
                    LabelText(
                        tile.title,
                        style = MaterialTheme.typography.labelLarge,
                        textAlign = TextAlign.Center,
                        maxScale = TILE_MAX_SCALE,
                    )
                    LabelText(
                        tile.hint,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSecondaryContainer.copy(alpha = 0.75f),
                        textAlign = TextAlign.Center,
                        maxScale = TILE_MAX_SCALE,
                    )
                }
            }
        }
    }
}

private class HomeTile(val icon: ImageVector, val title: String, val hint: String, val enabled: Boolean = true, val onClick: () -> Unit)

/** A third of a phone width fits "Library" / "Библиотека" only if the labels barely grow with the font. */
private const val TILE_MAX_SCALE = 1.1f

/** Nothing plays, so no tracks: the TV screen (where VLC, YouTube and Moonlight open) and "More". */
@Composable
private fun HomeQuickActions(ui: RemoteUiState, actions: RemoteActions) {
    val list = buildList {
        displayAction(ui) { actions.open(RemotePlace.DISPLAYS) }?.let(::add)
        add(QuickAction(Icons.Default.MoreHoriz, stringResource(R.string.chip_more), null, true) { actions.open(RemotePlace.MORE) })
    }
    QuickActionsRow(list)
}

/**
 * Its children top to bottom, as many as fit the height it is given; the rest are left out (the remote never
 * scrolls). The first child is a header: it is shown only with at least one entry under it.
 */
@Composable
private fun FittingColumn(spacing: Dp, modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Layout(content, modifier) { measurables, constraints ->
        val gap = spacing.roundToPx()
        val loose = constraints.copy(minHeight = 0)
        val shown = mutableListOf<Placeable>()
        var height = 0
        for (measurable in measurables) {
            val placeable = measurable.measure(loose)
            val total = height + (if (shown.isEmpty()) 0 else gap) + placeable.height
            if (constraints.hasBoundedHeight && total > constraints.maxHeight) break
            shown += placeable
            height = total
        }
        if (shown.size == 1 && measurables.size > 1) {
            shown.clear() // a header alone
            height = 0
        }
        val width = if (constraints.hasBoundedWidth) constraints.maxWidth else shown.maxOfOrNull { it.width } ?: 0
        layout(width, height.coerceAtLeast(constraints.minHeight)) {
            var y = 0
            shown.forEach {
                it.placeRelative(0, y)
                y += it.height + gap
            }
        }
    }
}

/**
 * The home screen's YouTube tile: a link to paste, the videos played on the TV, and the YouTube app to pick one in
 * (then "Share → Sofabird"); the add-on switches too.
 */
@Composable
internal fun YouTubeSheet(ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tick = rememberTick(ui.settings.haptics)
    val context = LocalContext.current
    val online = ui.connection.isOnline
    LaunchedEffect(Unit) { viewModel.loadPcInfo() }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Column(
            Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp)
                .padding(bottom = 32.dp),
        ) {
            YouTubeLinkSection(
                enabled = online,
                adBlock = ui.state.youtube?.adBlock,
                onAdBlock = { tick(); viewModel.sendSystem(SystemCommand.YouTubeAdBlock(it)) },
                sponsorBlock = ui.state.youtube?.sponsorBlock,
                onSponsorBlock = { tick(); viewModel.sendSystem(SystemCommand.YouTubeSponsorBlock(it)) },
                recent = ui.recentVideos,
                onRecent = { tick(); viewModel.send(PlayerCommand.Open(it.uri)); onDismiss() },
                onOpenApp = { openYouTubeApp(context) },
            ) {
                tick()
                viewModel.sendSystem(SystemCommand.YouTubeOpen(it))
                onDismiss()
            }
        }
    }
}

/** youtube.com opens the YouTube app where it is installed (the app claims those links), the browser otherwise. */
internal fun openYouTubeApp(context: Context) {
    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, YOUTUBE_URL.toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
}

private const val YOUTUBE_URL = "https://www.youtube.com/"
