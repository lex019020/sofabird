package app.sofabird.ui.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Audiotrack
import androidx.compose.material.icons.filled.FastForward
import androidx.compose.material.icons.filled.Fullscreen
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
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sofabird.R
import app.sofabird.data.history.WatchPrompts
import app.sofabird.data.player.PcWaker
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.backend.YouTubeAction
import app.sofabird.protocol.model.HistoryChange
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.protocol.model.TrackKind
import app.sofabird.protocol.model.TrackKind.AUDIO
import app.sofabird.protocol.model.TrackKind.SUBTITLE
import app.sofabird.protocol.model.VlcProcessStatus
import app.sofabird.protocol.model.YouTubeState
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.history.NextEpisodeCard
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
    fun openConnection() {}
    fun wakePc() {}
    fun open(place: RemotePlace) {}

    /** The home screen's menu of a "Continue watching" entry: mark it watched, remove it. */
    fun changeHistory(item: HistoryItem, change: HistoryChange) {}

    /** PC-side commands: end a Moonlight stream, control YouTube on the TV, the PC's volume. */
    fun system(command: SystemCommand) {}
}

/** Where the remote leads: its sheets (the TV screen, "More", YouTube) and other screens of the app. */
enum class RemotePlace { DISPLAYS, MORE, YOUTUBE, YOUTUBE_VIDEO, LIBRARY, HISTORY }

@Composable
fun RemoteScreen(
    onOpenConnection: () -> Unit,
    onOpenLibrary: () -> Unit = {},
    onOpenHistory: () -> Unit = {},
    viewModel: RemoteViewModel = koinViewModel(),
) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    val prompts: WatchPrompts = koinInject()
    val next by prompts.next.collectAsStateWithLifecycle()
    var tracksSheet by remember { mutableStateOf<TrackKind?>(null) }
    var moreSheet by remember { mutableStateOf(false) }
    var displaySheet by remember { mutableStateOf(false) }
    var youtubeSheet by remember { mutableStateOf(false) }
    var youtubeVideoSheet by remember { mutableStateOf(false) }
    val openConnection by rememberUpdatedState(onOpenConnection)
    val openLibrary by rememberUpdatedState(onOpenLibrary)
    val openHistory by rememberUpdatedState(onOpenHistory)

    val waker: PcWaker = koinInject()
    val actions = remember(viewModel, prompts, waker) {
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
            override fun openConnection() = openConnection()
            override fun wakePc() = waker.wake()
            override fun open(place: RemotePlace) = when (place) {
                RemotePlace.DISPLAYS -> displaySheet = true
                RemotePlace.MORE -> moreSheet = true
                RemotePlace.YOUTUBE -> youtubeSheet = true
                RemotePlace.YOUTUBE_VIDEO -> youtubeVideoSheet = true
                RemotePlace.LIBRARY -> openLibrary()
                RemotePlace.HISTORY -> openHistory()
            }
            override fun changeHistory(item: HistoryItem, change: HistoryChange) = viewModel.changeHistory(item, change)
            override fun system(command: SystemCommand) = viewModel.sendSystem(command)
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
    if (youtubeSheet) {
        YouTubeSheet(ui = ui, viewModel = viewModel, onDismiss = { youtubeSheet = false })
    }
    val youtube = ui.youtube
    if (youtubeVideoSheet && youtube != null) {
        val tick = rememberTick(ui.settings.haptics)
        YouTubeVideoSheet(youtube, ui.connection.isOnline, tick, viewModel::sendSystem, onDismiss = { youtubeVideoSheet = false })
    }
}

/**
 * The remote without its ViewModel: portrait (controls pinned at the bottom) or landscape (two columns). It never
 * scrolls (a project rule): the cover takes only the height the other blocks leave and gives way first.
 */
@Composable
fun RemoteContent(ui: RemoteUiState, next: NextEpisode?, actions: RemoteActions) {
    // What the TV shows decides the remote: a Moonlight stream, YouTube in the browser, or VLC.
    val streaming = ui.streamingHost
    val youtube = ui.youtube.takeIf { streaming == null }
    val block = ui.vlcBlock.takeIf { streaming == null && youtube == null }
    val onYouTube: (YouTubeAction, Double?) -> Unit = { action, time -> actions.system(SystemCommand.YouTube(action, time)) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val landscape = maxWidth > maxHeight
        if (ui.home) {
            RemoteHome(ui, next, actions, landscape)
        } else if (landscape) {
            Row(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
                Column(
                    Modifier
                        .weight(0.38f)
                        .fillMaxHeight(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Spacer(Modifier.height(8.dp))
                    NowOnTv(ui, streaming, youtube, block, next, actions, gap = 8.dp)
                    OfflineCard(ui, actions::openConnection, actions::wakePc)
                    BluetoothFallbackCard(ui, onReady = actions::bluetoothReady, onDismiss = actions::dismissBluetooth)
                }
                Spacer(Modifier.width(16.dp))
                Column(
                    Modifier
                        .weight(0.62f)
                        .fillMaxHeight(),
                    verticalArrangement = Arrangement.Center,
                ) {
                    when {
                        streaming != null -> Spacer(Modifier.height(8.dp))
                        youtube != null -> {
                            YouTubeScrubBar(youtube, ui.connection.isOnline) { onYouTube(YouTubeAction.SEEK, it) }
                            YouTubeTransport(youtube, ui, onYouTube)
                            YouTubeVolume(ui, actions)
                        }
                        block != null -> VlcStateCard(block, ui, onLaunch = actions::launchVlc)
                        else -> {
                            ScrubBar(ui, actions)
                            TransportRow(ui, actions)
                            VolumeRow(ui, actions)
                        }
                    }
                    BottomActions(ui, youtube, actions)
                }
            }
        } else {
            Column(
                Modifier
                    .fillMaxSize()
                    .padding(horizontal = 16.dp),
            ) {
                // The upper part: the title and the cards (next episode, offline, Bluetooth, VLC state) are measured
                // first, the cover takes the height they leave (keeping 16:9), and the block sits in the middle.
                // The playback controls below always stay on screen.
                Column(
                    Modifier
                        .weight(1f)
                        .fillMaxWidth(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Spacer(Modifier.height(8.dp))
                    NowOnTv(ui, streaming, youtube, block, next, actions, gap = 12.dp)
                    OfflineCard(ui, actions::openConnection, actions::wakePc)
                    BluetoothFallbackCard(ui, onReady = actions::bluetoothReady, onDismiss = actions::dismissBluetooth)
                    if (block != null) VlcStateCard(block, ui, onLaunch = actions::launchVlc)
                }
                BottomActions(ui, youtube, actions)
                when {
                    youtube != null -> {
                        YouTubeScrubBar(youtube, ui.connection.isOnline) { onYouTube(YouTubeAction.SEEK, it) }
                        YouTubeVolume(ui, actions)
                        Spacer(Modifier.height(4.dp))
                        YouTubeTransport(youtube, ui, onYouTube)
                    }
                    block == null && streaming == null -> {
                        ScrubBar(ui, actions)
                        VolumeRow(ui, actions)
                        Spacer(Modifier.height(4.dp))
                        TransportRow(ui, actions)
                    }
                }
                Spacer(Modifier.height(12.dp))
            }
        }
    }
}

/**
 * The upper block: what is on the TV. A Moonlight stream's card, YouTube's thumbnail and title, or VLC's cover,
 * title and next-episode offer. The cover takes the height the rest leaves.
 */
@Composable
private fun ColumnScope.NowOnTv(
    ui: RemoteUiState,
    streaming: String?,
    youtube: YouTubeState?,
    block: VlcProcessStatus?,
    next: NextEpisode?,
    actions: RemoteActions,
    gap: Dp,
) {
    when {
        streaming != null -> StreamingCard(streaming, ui.settings.haptics, onEnd = { actions.system(SystemCommand.MoonlightStop) })
        youtube != null -> {
            YouTubeArtwork(youtube, ui.settings.haptics, Modifier.weight(1f, fill = false)) {
                actions.system(SystemCommand.YouTube(YouTubeAction.CLOSE))
            }
            Spacer(Modifier.height(gap))
            YouTubeTitleBlock(youtube)
            SleepTimerUnderTitle(ui)
        }
        else -> {
            Artwork(ui, Modifier.weight(1f, fill = false), onStop = { actions.send(PlayerCommand.Stop) })
            Spacer(Modifier.height(gap))
            if (block == null) {
                TitleBlock(ui)
                SleepTimerUnderTitle(ui)
            }
            next?.let { NextEpisodeCard(it, ui.settings.haptics, onPlay = actions::playNext, onDismiss = actions::dismissNext) }
        }
    }
}

/** "Pauses in 23 min" under what plays while the sleep timer runs. */
@Composable
private fun SleepTimerUnderTitle(ui: RemoteUiState) {
    val timer = ui.state.system?.sleepTimer?.takeIf { ui.capabilities.sleepTimer } ?: return
    SleepTimerLine(timer, Modifier.padding(top = 4.dp))
}

/**
 * VLC's tracks, the TV screen and "More"; with YouTube on the TV "Skip" (while an ad can be skipped), "Video" (its
 * subtitles and quality, once the TV extension tells them — the TV screen then moves to "More"), "Fullscreen" and
 * "More" (VLC's tracks do not apply).
 */
@Composable
private fun BottomActions(ui: RemoteUiState, youtube: YouTubeState?, actions: RemoteActions) {
    if (youtube != null) {
        val online = ui.connection.isOnline
        val list = buildList {
            if (youtube.adSkippable) {
                val skip = stringResource(R.string.action_skip_ad)
                add(
                    QuickAction(Icons.Default.FastForward, stringResource(R.string.youtube_ad), skip, online) {
                        actions.system(SystemCommand.YouTube(YouTubeAction.SKIP_AD))
                    },
                )
            }
            val video = youTubeVideoSummary(youtube)
            if (video != null) {
                add(
                    QuickAction(Icons.Default.Subtitles, stringResource(R.string.youtube_video_tile), video, online) {
                        actions.open(RemotePlace.YOUTUBE_VIDEO)
                    },
                )
            }
            add(
                QuickAction(Icons.Default.Fullscreen, stringResource(R.string.action_youtube_fullscreen), null, online) {
                    actions.system(SystemCommand.YouTube(YouTubeAction.FULLSCREEN))
                },
            )
            if (video == null) displayAction(ui) { actions.open(RemotePlace.DISPLAYS) }?.let(::add)
            add(QuickAction(Icons.Default.MoreHoriz, stringResource(R.string.chip_more), null, true) { actions.open(RemotePlace.MORE) })
        }
        QuickActionsRow(list)
    } else {
        QuickActions(
            ui,
            onTracks = actions::openTracks,
            onDisplays = { actions.open(RemotePlace.DISPLAYS) },
            onMore = { actions.open(RemotePlace.MORE) },
        )
    }
}

/** The TV screen: where VLC is now, or (VLC closed) where VLC, YouTube and Moonlight open. Usable while VLC is closed. */
@Composable
internal fun displayAction(ui: RemoteUiState, onDisplays: () -> Unit): QuickAction? {
    if (!ui.showDisplayChoice) return null
    val shown = ui.displayChoice.chipDisplay
    val title = stringResource(R.string.section_display)
    return QuickAction(displayIcon(shown), title, shown?.let { displayLabel(it) }, ui.connection.isOnline, onDisplays)
}

/** The PC's volume for YouTube (the browser plays through Windows, not VLC). */
@Composable
private fun YouTubeVolume(ui: RemoteUiState, actions: RemoteActions) {
    if (!ui.capabilities.systemVolume) return
    val system = ui.state.system
    SystemVolumeRow(
        volume = system?.volume,
        muted = system?.muted == true,
        enabled = ui.connection.isOnline,
        onSet = { actions.system(SystemCommand.SystemVolume(value = it.coerceIn(0, 100))) },
        onToggleMute = { actions.system(SystemCommand.SystemVolume(mute = system?.muted != true)) },
    )
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
        displayAction(ui, onDisplays)?.let(::add)
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
