package app.sofabird.ui.remote

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.RemoteButton
import app.sofabird.ui.common.formatRemaining
import app.sofabird.ui.common.formatTime
import app.sofabird.ui.common.rememberTick
import kotlinx.coroutines.delay

@Composable
internal fun ScrubBar(ui: RemoteUiState, actions: RemoteActions) {
    val length = ui.state.playback.length
    val enabled = ui.connection.isOnline && length > 0 && !ui.state.isStopped
    val live by rememberLivePosition(ui.state)
    val pos = (ui.scrubTarget ?: live).coerceIn(0.0, length.toDouble().coerceAtLeast(0.0))
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
                onValueChange = { actions.onScrub(it.toDouble()) },
                onValueChangeFinished = { actions.onScrubFinished() },
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

/**
 * The position between status updates, in seconds: interpolated 4 times a second while playing. It lives here, not in
 * the ViewModel, so the ticks recompose only the scrub bar.
 */
@Composable
private fun rememberLivePosition(state: PlayerState): State<Double> =
    produceState(state.interpolatedTime(System.currentTimeMillis()), state) {
        // The initial value is used only once, at the first composition: every new state sets it here, or a pause
        // would keep showing the position of the very first state (0 before the connection).
        value = state.interpolatedTime(System.currentTimeMillis())
        if (!state.isPlaying || state.playback.updatedAt <= 0L) return@produceState
        while (true) {
            delay(POSITION_TICK_MS)
            value = state.interpolatedTime(System.currentTimeMillis())
        }
    }

private const val POSITION_TICK_MS = 250L

@Composable
internal fun TransportRow(ui: RemoteUiState, actions: RemoteActions) {
    val short = ui.settings.seekShortSec.toLong()
    val long = ui.settings.seekLongSec.toLong()
    TransportButtons(
        playing = ui.state.playback.state == PlaybackStatus.PLAYING,
        enabled = ui.connection.isOnline,
        haptics = ui.settings.haptics,
        backLabel = "−$short",
        forwardLabel = "+$long",
        onBack = { actions.seekBy(-short) },
        onPrevious = { actions.send(PlayerCommand.Prev) },
        onPlayPause = { actions.togglePause() },
        onNext = { actions.send(PlayerCommand.Next) },
        onForward = { actions.seekBy(long) },
    )
}

/**
 * The five round transport buttons: back, previous, play/pause, next, forward. Four 64 dp buttons and the 88 dp play
 * button need 344 dp plus gaps; a 360 dp phone has less once the screen padding is taken, so all five scale together
 * and stay round and alike instead of the last ones squeezing. Shared by VLC and YouTube on the TV.
 */
@Composable
internal fun TransportButtons(
    playing: Boolean,
    enabled: Boolean,
    haptics: Boolean,
    backLabel: String,
    forwardLabel: String,
    onBack: () -> Unit,
    onPrevious: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onForward: () -> Unit,
    previousEnabled: Boolean = enabled,
    nextEnabled: Boolean = enabled,
) {
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        BoxWithConstraints(Modifier.fillMaxWidth()) {
            val needed = TRANSPORT_BUTTON * 4 + TRANSPORT_PLAY + TRANSPORT_MIN_GAP * 4
            val scale = (maxWidth / needed).coerceAtMost(1f)
            val button = TRANSPORT_BUTTON * scale
            val icon = 32.dp * scale
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                RemoteButton(
                    icon = Icons.Default.Replay10,
                    contentDescription = stringResource(R.string.action_seek_back),
                    onClick = onBack,
                    size = button,
                    iconSize = icon,
                    label = backLabel,
                    enabled = enabled,
                    repeatOnHold = true,
                    haptics = haptics,
                )
                RemoteButton(
                    icon = Icons.Default.SkipPrevious,
                    contentDescription = stringResource(R.string.action_prev),
                    onClick = onPrevious,
                    size = button,
                    iconSize = icon,
                    enabled = previousEnabled,
                    haptics = haptics,
                )
                RemoteButton(
                    icon = if (playing) Icons.Default.Pause else Icons.Default.PlayArrow,
                    contentDescription = stringResource(if (playing) R.string.action_pause else R.string.action_play),
                    onClick = onPlayPause,
                    size = TRANSPORT_PLAY * scale,
                    iconSize = 44.dp * scale,
                    filled = true,
                    enabled = enabled,
                    haptics = haptics,
                )
                RemoteButton(
                    icon = Icons.Default.SkipNext,
                    contentDescription = stringResource(R.string.action_next),
                    onClick = onNext,
                    size = button,
                    iconSize = icon,
                    enabled = nextEnabled,
                    haptics = haptics,
                )
                RemoteButton(
                    icon = Icons.Default.Forward30,
                    contentDescription = stringResource(R.string.action_seek_forward),
                    onClick = onForward,
                    size = button,
                    iconSize = icon,
                    label = forwardLabel,
                    enabled = enabled,
                    repeatOnHold = true,
                    haptics = haptics,
                )
            }
        }
    }
}

@Composable
internal fun VolumeRow(ui: RemoteUiState, actions: RemoteActions) {
    val volume = ui.state.volume
    val online = ui.connection.isOnline
    val tick = rememberTick(ui.settings.haptics)
    var dragging by remember { mutableStateOf<Float?>(null) }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { tick(); actions.toggleMute() }, enabled = online) {
            Icon(
                if (volume.muted || volume.value == 0) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                contentDescription = stringResource(R.string.action_mute),
            )
        }
        Slider(
            value = dragging ?: volume.value.toFloat(),
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { actions.setVolume(it.toInt()) }
                dragging = null
            },
            valueRange = 0f..512f,
            enabled = online,
            modifier = Modifier.weight(1f),
        )
        val shown = ((dragging ?: volume.value.toFloat()) * 100 / 256).toInt()
        LabelText(
            "$shown %",
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.widthIn(min = 56.dp),
            textAlign = TextAlign.End,
            color = if (shown > 100) MaterialTheme.colorScheme.tertiary else Color.Unspecified,
        )
    }
}

private val TRANSPORT_BUTTON = 64.dp
private val TRANSPORT_PLAY = 88.dp
private val TRANSPORT_MIN_GAP = 6.dp
