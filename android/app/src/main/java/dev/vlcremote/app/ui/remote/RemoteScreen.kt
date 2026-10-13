package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Audiotrack
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Subtitles
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.data.history.WatchPrompts
import dev.vlcremote.app.ui.history.NextEpisodeCard
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.NextEpisode
import dev.vlcremote.protocol.model.TrackKind
import dev.vlcremote.protocol.model.TrackKind.AUDIO
import dev.vlcremote.protocol.model.TrackKind.SUBTITLE
import org.koin.androidx.compose.koinViewModel
import org.koin.compose.koinInject

/**
 * What the remote's content asks for. The screen routes it to the ViewModel, the watch prompts and its sheets;
 * previews and screenshot tests use the no-op defaults.
 */
interface RemoteActions {
    fun send(command: PlayerCommand) {}
    fun togglePause() {}
    fun seekBy(delta: Long) {}
    fun onScrub(seconds: Double) {}
    fun onScrubFinished() {}
    fun setVolume(value: Int) {}
    fun toggleMute() {}
    fun launchVlc() {}
    fun bluetoothReady() {}
    fun dismissBluetooth() {}
    fun playNext() {}
    fun dismissNext() {}
    fun openTracks(kind: TrackKind) {}
    fun openDisplays() {}
    fun openMore() {}
    fun openConnection() {}
}

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
    val openConnection by rememberUpdatedState(onOpenConnection)

    val actions = remember(viewModel, prompts) {
        object : RemoteActions {
            override fun send(command: PlayerCommand) = viewModel.send(command)
            override fun togglePause() = viewModel.togglePause()
            override fun seekBy(delta: Long) = viewModel.seekBy(delta)
            override fun onScrub(seconds: Double) = viewModel.onScrub(seconds)
            override fun onScrubFinished() = viewModel.onScrubFinished()
            override fun setVolume(value: Int) = viewModel.setVolume(value)
            override fun toggleMute() = viewModel.toggleMute()
            override fun launchVlc() = viewModel.launchVlc()
            override fun bluetoothReady() = viewModel.bluetoothReady()
            override fun dismissBluetooth() = viewModel.dismissBluetooth()
            override fun playNext() = prompts.playNext()
            override fun dismissNext() = prompts.dismissNext()
            override fun openTracks(kind: TrackKind) {
                tracksSheet = kind
            }
            override fun openDisplays() {
                displaySheet = true
            }
            override fun openMore() {
                moreSheet = true
            }
            override fun openConnection() = openConnection()
        }
    }

    RemoteContent(ui, next, actions)

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

/** The remote without its ViewModel: portrait (controls pinned at the bottom) or landscape (two columns). */
@Composable
fun RemoteContent(ui: RemoteUiState, next: NextEpisode?, actions: RemoteActions) {
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
                    next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = actions::playNext, onDismiss = actions::dismissNext) }
                    OfflineCard(ui, actions::openConnection)
                    BluetoothFallbackCard(ui, onReady = actions::bluetoothReady, onDismiss = actions::dismissBluetooth)
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
                        VlcStateCard(block, ui, onLaunch = actions::launchVlc)
                    } else {
                        ScrubBar(ui, actions)
                        TransportRow(ui, actions)
                        VolumeRow(ui, actions)
                    }
                    QuickActions(ui, onTracks = actions::openTracks, onDisplays = actions::openDisplays, onMore = actions::openMore)
                }
            }
        } else {
            Column(
                Modifier
                    .fillMaxSize()
                    .padding(horizontal = 16.dp),
            ) {
                val block = ui.vlcBlock
                // The upper part scrolls; the playback controls below always stay on screen. The artwork
                // gives way first: it takes at most 40 % of the free height.
                BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
                    val artworkMaxHeight = maxHeight * 0.4f
                    Column(
                        Modifier
                            .fillMaxSize()
                            .verticalScroll(rememberScrollState()),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Spacer(Modifier.height(8.dp))
                        Artwork(ui, Modifier.widthIn(max = 360.dp).heightIn(max = artworkMaxHeight))
                        Spacer(Modifier.height(12.dp))
                        if (block == null) TitleBlock(ui)
                        next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = actions::playNext, onDismiss = actions::dismissNext) }
                        OfflineCard(ui, actions::openConnection)
                        BluetoothFallbackCard(ui, onReady = actions::bluetoothReady, onDismiss = actions::dismissBluetooth)
                        if (block != null) VlcStateCard(block, ui, onLaunch = actions::launchVlc)
                    }
                }
                QuickActions(ui, onTracks = actions::openTracks, onDisplays = actions::openDisplays, onMore = actions::openMore)
                if (block == null) {
                    ScrubBar(ui, actions)
                    VolumeRow(ui, actions)
                    Spacer(Modifier.height(4.dp))
                    TransportRow(ui, actions)
                }
                Spacer(Modifier.height(12.dp))
            }
        }
    }
}

@Composable
private fun QuickActions(ui: RemoteUiState, onTracks: (TrackKind) -> Unit, onDisplays: () -> Unit, onMore: () -> Unit) {
    val tracks = ui.state.tracks
    val audioLabel = trackChipLabel(
        tracks.audio.firstOrNull { it.id == tracks.currentAudio }?.let { it.lang ?: it.name },
        tracks.currentAudio,
        tracks.audio.size,
    )
    val subLabel = trackChipLabel(
        tracks.subtitle.firstOrNull { it.id == tracks.currentSubtitle }?.let { it.lang ?: it.name },
        tracks.currentSubtitle,
        tracks.subtitle.size,
    )
    val tracksEnabled = ui.connection.isOnline && ui.vlcBlock == null
    val actions = buildList {
        add(QuickAction(Icons.Default.Audiotrack, stringResource(R.string.quick_audio), audioLabel, tracksEnabled) { onTracks(AUDIO) })
        add(QuickAction(Icons.Default.Subtitles, stringResource(R.string.quick_subtitles), subLabel, tracksEnabled) { onTracks(SUBTITLE) })
        if (ui.showDisplayChoice) {
            // Where VLC is now, or (VLC closed) where it will open. Usable while VLC is closed too.
            val shown = ui.displayChoice.chipDisplay
            val title = stringResource(R.string.section_display)
            add(QuickAction(displayIcon(shown), title, shown?.let { displayLabel(it) }, ui.connection.isOnline, onDisplays))
        }
        add(QuickAction(Icons.Default.MoreHoriz, stringResource(R.string.chip_more), null, true, onMore))
    }
    QuickActionsRow(actions)
}

@Composable
private fun trackChipLabel(current: String?, currentId: Int?, count: Int): String = when {
    currentId == -1 -> stringResource(R.string.track_off)
    current != null -> current
    currentId == null && count > 0 -> stringResource(R.string.track_unknown_count, count)
    count == 0 -> "—"
    else -> "#$currentId"
}
