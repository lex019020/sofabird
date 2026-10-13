package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Audiotrack
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Subtitles
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil3.compose.AsyncImage
import dev.vlcremote.app.R
import dev.vlcremote.app.data.companion.BluetoothStatus
import dev.vlcremote.app.data.history.WatchPrompts
import dev.vlcremote.app.ui.history.NextEpisodeCard
import dev.vlcremote.app.ui.common.BluetoothPermissionDeniedDialog
import dev.vlcremote.app.ui.common.rememberBluetoothRequests
import dev.vlcremote.app.ui.common.RemoteButton
import dev.vlcremote.app.ui.common.formatRemaining
import dev.vlcremote.app.ui.common.formatTime
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.PlaybackStatus
import dev.vlcremote.protocol.model.TrackKind
import org.koin.androidx.compose.koinViewModel
import org.koin.compose.koinInject

@Composable
fun RemoteScreen(
    onOpenConnection: () -> Unit,
    viewModel: RemoteViewModel = koinViewModel(),
) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val prompts: WatchPrompts = koinInject()
    val next by prompts.next.collectAsStateWithLifecycle()
    var tracksSheet by remember { mutableStateOf<TrackKind?>(null) }
    var moreSheet by remember { mutableStateOf(false) }
    var displaySheet by remember { mutableStateOf(false) }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val landscape = maxWidth > maxHeight
        if (landscape) {
            Row(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
                Column(
                    Modifier
                        .weight(0.38f)
                        .fillMaxHeight()
                        .verticalScroll(rememberScrollState()),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Spacer(Modifier.height(8.dp))
                    Artwork(ui, Modifier.fillMaxWidth(0.8f))
                    Spacer(Modifier.height(8.dp))
                    if (ui.vlcBlock == null) TitleBlock(ui)
                    next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = prompts::playNext, onDismiss = prompts::dismissNext) }
                    OfflineCard(ui, onOpenConnection)
                    BluetoothFallbackCard(ui, onReady = viewModel::bluetoothReady, onDismiss = viewModel::dismissBluetooth)
                }
                Spacer(Modifier.width(16.dp))
                Column(
                    Modifier
                        .weight(0.62f)
                        .fillMaxHeight()
                        .verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.Center,
                ) {
                    val block = ui.vlcBlock
                    if (block != null) {
                        VlcStateCard(block, ui, onLaunch = viewModel::launchVlc)
                    } else {
                        ScrubBar(ui, viewModel)
                        TransportRow(ui, viewModel)
                        VolumeRow(ui, viewModel)
                    }
                    QuickChips(ui, onTracks = { tracksSheet = it }, onDisplays = { displaySheet = true }, onMore = { moreSheet = true })
                }
            }
        } else {
            Column(
                Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Spacer(Modifier.height(8.dp))
                Artwork(ui, Modifier.fillMaxWidth().widthIn(max = 360.dp))
                Spacer(Modifier.height(12.dp))
                val block = ui.vlcBlock
                if (block == null) TitleBlock(ui)
                next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = prompts::playNext, onDismiss = prompts::dismissNext) }
                OfflineCard(ui, onOpenConnection)
                BluetoothFallbackCard(ui, onReady = viewModel::bluetoothReady, onDismiss = viewModel::dismissBluetooth)
                if (block != null) VlcStateCard(block, ui, onLaunch = viewModel::launchVlc)
                QuickChips(ui, onTracks = { tracksSheet = it }, onDisplays = { displaySheet = true }, onMore = { moreSheet = true })
                Spacer(Modifier.weight(1f))
                if (block == null) {
                    ScrubBar(ui, viewModel)
                    Spacer(Modifier.height(8.dp))
                    VolumeRow(ui, viewModel)
                    Spacer(Modifier.height(8.dp))
                    TransportRow(ui, viewModel)
                }
                Spacer(Modifier.height(16.dp))
            }
        }
    }

    tracksSheet?.let { kind ->
        TracksSheet(kind = kind, ui = ui, viewModel = viewModel, onDismiss = { tracksSheet = null })
    }
    if (moreSheet) {
        MoreSheet(
            ui = ui,
            viewModel = viewModel,
            onDismiss = { moreSheet = false },
            onOpenDisplays = {
                moreSheet = false
                displaySheet = true
            },
        )
    }
    if (displaySheet && ui.showDisplayChoice) {
        DisplaySheet(ui = ui, viewModel = viewModel, onDismiss = { displaySheet = false })
    }
}

@Composable
private fun Artwork(ui: RemoteUiState, modifier: Modifier) {
    Box(
        modifier
            .aspectRatio(16f / 9f)
            .clip(RoundedCornerShape(16.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        val art = ui.state.item.artUrl
        if (art != null) {
            AsyncImage(
                model = art,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Icon(
                Icons.Default.Movie,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                modifier = Modifier.fillMaxSize(0.3f),
            )
        }
    }
}

@Composable
private fun TitleBlock(ui: RemoteUiState) {
    val title = ui.state.item.title ?: when {
        !ui.connection.isOnline -> stringResource(R.string.not_connected_title)
        !ui.state.vlc.running && ui.isCompanion -> stringResource(R.string.vlc_not_running_title)
        else -> stringResource(R.string.nothing_playing)
    }
    Text(
        title,
        style = MaterialTheme.typography.titleLarge,
        maxLines = 2,
        overflow = TextOverflow.Ellipsis,
        textAlign = TextAlign.Center,
        modifier = Modifier.fillMaxWidth(),
    )
    val ch = ui.state.chapters
    if (ch.count > 0 && ch.index >= 0) {
        val name = ch.names?.getOrNull(ch.index)
        Text(
            if (name.isNullOrBlank()) stringResource(R.string.chapter_n_of, ch.index + 1, ch.count)
            else stringResource(R.string.chapter_n_named, ch.index + 1, ch.count, name),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun OfflineCard(ui: RemoteUiState, onOpenConnection: () -> Unit) {
    if (ui.connection is ConnectionState.Failed || ui.connection is ConnectionState.Disconnected) {
        Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
            Column(Modifier.padding(16.dp)) {
                Text(
                    if (ui.hostName != null) stringResource(R.string.no_link_with, ui.hostName) else stringResource(R.string.no_server_configured),
                    style = MaterialTheme.typography.titleMedium,
                )
                Spacer(Modifier.height(8.dp))
                Button(onClick = onOpenConnection) { Text(stringResource(R.string.action_connect)) }
            }
        }
    }
}

/**
 * The LAN is down and the PC could be reached over Bluetooth once the user grants "Nearby devices"
 * or turns Bluetooth on. The card is the in-context rationale; "Allow" opens the system dialog.
 */
@Composable
private fun BluetoothFallbackCard(ui: RemoteUiState, onReady: () -> Unit, onDismiss: () -> Unit) {
    val need = ui.bluetoothNeed ?: return
    if (ui.connection.isOnline) return
    var denied by remember { mutableStateOf(false) }
    val requests = rememberBluetoothRequests(
        onPermissionResult = { granted -> if (granted) onReady() else denied = true },
        onEnableResult = { on -> if (on) onReady() },
    )
    Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Bluetooth, null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(8.dp))
                Text(
                    stringResource(R.string.bt_fallback_title, ui.hostName.orEmpty()),
                    style = MaterialTheme.typography.titleMedium,
                )
            }
            Spacer(Modifier.height(6.dp))
            Text(
                stringResource(
                    if (need == BluetoothStatus.OFF) R.string.bt_fallback_off_body else R.string.bt_fallback_permission_body,
                ),
                style = MaterialTheme.typography.bodyMedium,
            )
            Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = onDismiss) { Text(stringResource(R.string.not_now)) }
                Spacer(Modifier.width(8.dp))
                if (need == BluetoothStatus.OFF) {
                    Button(onClick = requests.requestEnable) { Text(stringResource(R.string.bt_turn_on)) }
                } else {
                    Button(onClick = requests.requestPermission) { Text(stringResource(R.string.bt_permission_allow)) }
                }
            }
        }
    }
    if (denied) {
        BluetoothPermissionDeniedDialog(
            onOpenSettings = {
                denied = false
                requests.openAppSettings()
            },
            onDismiss = { denied = false },
        )
    }
}

@Composable
private fun ScrubBar(ui: RemoteUiState, viewModel: RemoteViewModel) {
    val length = ui.state.playback.length
    val enabled = ui.connection.isOnline && length > 0 && !ui.state.isStopped
    val pos = ui.displayedPosition.coerceIn(0.0, length.toDouble().coerceAtLeast(0.0))
    Column(Modifier.fillMaxWidth()) {
        Box(Modifier.fillMaxWidth()) {
            // Chapter ticks
            val ch = ui.state.chapters
            if (ch.count > 1 && length > 0) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 10.dp)
                        .align(Alignment.Center)
                        .height(10.dp),
                ) {
                    // Chapters have no known offsets in VLC's HTTP API; show evenly spaced ticks
                    // when names are unavailable so the count is still visible.
                    repeat(ch.count) { i ->
                        Box(
                            Modifier
                                .weight(1f)
                                .fillMaxHeight(),
                            contentAlignment = Alignment.CenterStart,
                        ) {
                            if (i > 0) {
                                Box(
                                    Modifier
                                        .width(2.dp)
                                        .height(10.dp)
                                        .background(
                                            if (i <= ch.index) MaterialTheme.colorScheme.primary.copy(alpha = 0.6f)
                                            else MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.35f),
                                        ),
                                )
                            }
                        }
                    }
                }
            }
            Slider(
                value = pos.toFloat(),
                onValueChange = { viewModel.onScrub(it.toDouble()) },
                onValueChangeFinished = { viewModel.onScrubFinished() },
                valueRange = 0f..(length.toFloat().coerceAtLeast(1f)),
                enabled = enabled,
                modifier = Modifier.fillMaxWidth(),
            )
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(formatTime(pos), style = MaterialTheme.typography.labelMedium)
            Text(formatRemaining(pos, length), style = MaterialTheme.typography.labelMedium)
            Text(formatTime(length), style = MaterialTheme.typography.labelMedium)
        }
    }
}

@Composable
private fun TransportRow(ui: RemoteUiState, viewModel: RemoteViewModel) {
    val online = ui.connection.isOnline
    val haptics = ui.settings.haptics
    val playing = ui.state.playback.state == PlaybackStatus.PLAYING
    val short = ui.settings.seekShortSec.toLong()
    val long = ui.settings.seekLongSec.toLong()
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceEvenly,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            RemoteButton(
                icon = Icons.Default.Replay10,
                contentDescription = stringResource(R.string.action_seek_back),
                onClick = { viewModel.seekBy(-short) },
                label = "−$short",
                enabled = online,
                repeatOnHold = true,
                haptics = haptics,
            )
            RemoteButton(
                icon = Icons.Default.SkipPrevious,
                contentDescription = stringResource(R.string.action_prev),
                onClick = { viewModel.send(PlayerCommand.Prev) },
                enabled = online,
                haptics = haptics,
            )
            RemoteButton(
                icon = if (playing) Icons.Default.Pause else Icons.Default.PlayArrow,
                contentDescription = stringResource(if (playing) R.string.action_pause else R.string.action_play),
                onClick = { viewModel.togglePause() },
                size = 88.dp,
                iconSize = 44.dp,
                filled = true,
                enabled = online,
                haptics = haptics,
            )
            RemoteButton(
                icon = Icons.Default.SkipNext,
                contentDescription = stringResource(R.string.action_next),
                onClick = { viewModel.send(PlayerCommand.Next) },
                enabled = online,
                haptics = haptics,
            )
            RemoteButton(
                icon = Icons.Default.Forward30,
                contentDescription = stringResource(R.string.action_seek_forward),
                onClick = { viewModel.seekBy(long) },
                label = "+$long",
                enabled = online,
                repeatOnHold = true,
                haptics = haptics,
            )
        }
        Spacer(Modifier.height(8.dp))
        RemoteButton(
            icon = Icons.Default.Stop,
            contentDescription = stringResource(R.string.action_stop),
            onClick = { viewModel.send(PlayerCommand.Stop) },
            size = 44.dp,
            iconSize = 22.dp,
            enabled = online && !ui.state.isStopped,
            haptics = haptics,
        )
    }
}

@Composable
private fun VolumeRow(ui: RemoteUiState, viewModel: RemoteViewModel) {
    val volume = ui.state.volume
    val online = ui.connection.isOnline
    val tick = rememberTick(ui.settings.haptics)
    var dragging by remember { mutableStateOf<Float?>(null) }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { tick(); viewModel.toggleMute() }, enabled = online) {
            Icon(
                if (volume.muted || volume.value == 0) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                contentDescription = stringResource(R.string.action_mute),
            )
        }
        Slider(
            value = dragging ?: volume.value.toFloat(),
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { viewModel.setVolume(it.toInt()) }
                dragging = null
            },
            valueRange = 0f..512f,
            enabled = online,
            modifier = Modifier.weight(1f),
        )
        val shown = ((dragging ?: volume.value.toFloat()) * 100 / 256).toInt()
        Text(
            "$shown %",
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.width(56.dp),
            textAlign = TextAlign.End,
            color = if (shown > 100) MaterialTheme.colorScheme.tertiary else Color.Unspecified,
        )
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun QuickChips(ui: RemoteUiState, onTracks: (TrackKind) -> Unit, onDisplays: () -> Unit, onMore: () -> Unit) {
    val tracks = ui.state.tracks
    val audioLabel = trackChipLabel(tracks.audio.firstOrNull { it.id == tracks.currentAudio }?.let { it.lang ?: it.name }, tracks.currentAudio, tracks.audio.size)
    val subLabel = trackChipLabel(tracks.subtitle.firstOrNull { it.id == tracks.currentSubtitle }?.let { it.lang ?: it.name }, tracks.currentSubtitle, tracks.subtitle.size)
    // Wraps to a second line on narrow screens (the "Display" chip makes four).
    FlowRow(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
    ) {
        AssistChip(
            onClick = { onTracks(TrackKind.AUDIO) },
            label = { Text(stringResource(R.string.chip_audio, audioLabel), maxLines = 1, overflow = TextOverflow.Ellipsis) },
            leadingIcon = { Icon(Icons.Default.Audiotrack, null) },
            enabled = ui.connection.isOnline && ui.vlcBlock == null,
            modifier = Modifier.widthIn(max = 220.dp),
        )
        AssistChip(
            onClick = { onTracks(TrackKind.SUBTITLE) },
            label = { Text(stringResource(R.string.chip_subtitles, subLabel), maxLines = 1, overflow = TextOverflow.Ellipsis) },
            leadingIcon = { Icon(Icons.Default.Subtitles, null) },
            enabled = ui.connection.isOnline && ui.vlcBlock == null,
            modifier = Modifier.widthIn(max = 220.dp),
        )
        if (ui.showDisplayChoice) {
            // Where VLC is now, or (VLC closed) where it will open. Usable while VLC is closed too.
            val shown = ui.displayChoice.chipDisplay
            AssistChip(
                onClick = onDisplays,
                label = {
                    Text(
                        shown?.let { stringResource(R.string.chip_display, displayLabel(it)) } ?: stringResource(R.string.section_display),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                },
                leadingIcon = { Icon(displayIcon(shown), null) },
                enabled = ui.connection.isOnline,
                modifier = Modifier.widthIn(max = 240.dp),
            )
        }
        AssistChip(
            onClick = onMore,
            label = { Text(stringResource(R.string.chip_more)) },
            leadingIcon = { Icon(Icons.Default.MoreHoriz, null) },
        )
    }
}

@Composable
private fun trackChipLabel(current: String?, currentId: Int?, count: Int): String = when {
    currentId == -1 -> stringResource(R.string.track_off)
    current != null -> current
    currentId == null && count > 0 -> stringResource(R.string.track_unknown_count, count)
    count == 0 -> "—"
    else -> "#$currentId"
}
