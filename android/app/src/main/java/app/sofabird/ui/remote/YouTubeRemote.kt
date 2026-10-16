package app.sofabird.ui.remote

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.SmartDisplay
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.YouTubeAction
import app.sofabird.protocol.model.YouTubeState
import app.sofabird.ui.common.formatRemaining
import app.sofabird.ui.common.formatTime
import app.sofabird.ui.common.rememberTick
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay

/** The video's thumbnail where the movie's cover is, with "Close YouTube" in its corner (VLC is back behind it). */
@Composable
internal fun YouTubeArtwork(yt: YouTubeState, haptics: Boolean, modifier: Modifier, onClose: () -> Unit) {
    Box(
        modifier
            .aspectRatio(16f / 9f)
            .clip(RoundedCornerShape(16.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        val thumbnail = yt.thumbnailUrl
        if (thumbnail != null) {
            AsyncImage(model = thumbnail, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        } else {
            Icon(
                Icons.Default.SmartDisplay,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                modifier = Modifier.fillMaxSize(0.3f),
            )
        }
        val tick = rememberTick(haptics)
        Box(
            Modifier
                .align(Alignment.TopEnd)
                .padding(6.dp)
                .size(40.dp)
                .clip(CircleShape)
                .background(Color.Black.copy(alpha = 0.55f))
                .clickable(onClickLabel = stringResource(R.string.action_youtube_close)) {
                    tick()
                    onClose()
                },
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Default.Close, stringResource(R.string.action_youtube_close), tint = Color.White, modifier = Modifier.size(22.dp))
        }
    }
}

/** The video's title and channel ("YouTube" until the browser reports them). */
@Composable
internal fun YouTubeTitleBlock(yt: YouTubeState) {
    Text(
        yt.title ?: stringResource(R.string.youtube_on_tv),
        style = MaterialTheme.typography.titleLarge,
        maxLines = 2,
        overflow = TextOverflow.Ellipsis,
        textAlign = TextAlign.Center,
        modifier = Modifier.fillMaxWidth(),
    )
    yt.channel?.let {
        Text(
            it,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
        )
    }
}

/** Position and seeking; the position moves on between reports while playing. */
@Composable
internal fun YouTubeScrubBar(yt: YouTubeState, enabled: Boolean, onSeek: (Double) -> Unit) {
    val length = yt.length ?: 0.0
    val live by rememberYouTubePosition(yt)
    var dragging by remember { mutableStateOf<Float?>(null) }
    val pos = (dragging?.toDouble() ?: live).coerceIn(0.0, length.coerceAtLeast(0.0))
    Column(Modifier.fillMaxWidth()) {
        Slider(
            value = pos.toFloat(),
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { onSeek(it.toDouble()) }
                dragging = null
            },
            valueRange = 0f..length.toFloat().coerceAtLeast(1f),
            enabled = enabled && length > 0,
            modifier = Modifier.fillMaxWidth(),
        )
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(formatTime(pos), style = MaterialTheme.typography.labelMedium)
            Text(formatRemaining(pos, length.toLong()), style = MaterialTheme.typography.labelMedium)
            Text(formatTime(length), style = MaterialTheme.typography.labelMedium)
        }
    }
}

@Composable
private fun rememberYouTubePosition(yt: YouTubeState): State<Double> =
    produceState(yt.interpolatedTime(System.currentTimeMillis()), yt) {
        value = yt.interpolatedTime(System.currentTimeMillis())
        if (!yt.playing || yt.updatedAt <= 0L) return@produceState
        while (true) {
            delay(POSITION_TICK_MS)
            value = yt.interpolatedTime(System.currentTimeMillis())
        }
    }

/** A seek sent from the phone: the target and when it was sent. */
private class PendingSeek(val target: Double, val atMs: Long)

/**
 * Back / previous / play-pause / next / forward for the video on the TV. The TV reports the new position a moment after
 * a seek, so presses in a row add up from the last target rather than from the old position.
 */
@Composable
internal fun YouTubeTransport(yt: YouTubeState, ui: RemoteUiState, onAction: (YouTubeAction, Double?) -> Unit) {
    val short = ui.settings.seekShortSec
    val long = ui.settings.seekLongSec
    val online = ui.connection.isOnline
    var pending by remember { mutableStateOf<PendingSeek?>(null) }
    fun seekBy(delta: Int) {
        val nowMs = System.currentTimeMillis()
        val base = pending?.takeIf { nowMs - it.atMs < PENDING_SEEK_MS }
            ?.let { it.target + if (yt.playing) (nowMs - it.atMs) / 1000.0 else 0.0 }
            ?: yt.interpolatedTime(nowMs)
        val target = (base + delta).coerceIn(0.0, yt.length ?: Double.MAX_VALUE)
        pending = PendingSeek(target, nowMs)
        onAction(YouTubeAction.SEEK, target)
    }
    TransportButtons(
        playing = yt.playing,
        enabled = online,
        haptics = ui.settings.haptics,
        backLabel = "−$short",
        forwardLabel = "+$long",
        onBack = { seekBy(-short) },
        onPrevious = { onAction(YouTubeAction.PREVIOUS, null) },
        onPlayPause = { onAction(YouTubeAction.TOGGLE, null) },
        onNext = { onAction(YouTubeAction.NEXT, null) },
        onForward = { seekBy(long) },
        previousEnabled = online && yt.canPrevious,
        nextEnabled = online && yt.canNext,
    )
}

/** The PC's volume while YouTube plays (VLC's own volume does not apply to the browser). */
@Composable
internal fun SystemVolumeRow(volume: Int?, muted: Boolean, enabled: Boolean, onSet: (Int) -> Unit, onToggleMute: () -> Unit) {
    var dragging by remember { mutableStateOf<Float?>(null) }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = onToggleMute, enabled = enabled) {
            Icon(
                if (muted || volume == 0) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                contentDescription = stringResource(R.string.action_mute),
            )
        }
        Slider(
            value = dragging ?: (volume ?: 0).toFloat(),
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { onSet(it.toInt()) }
                dragging = null
            },
            valueRange = 0f..100f,
            enabled = enabled && volume != null,
            modifier = Modifier.weight(1f),
        )
        Text(
            "${(dragging ?: (volume ?: 0).toFloat()).toInt()} %",
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.padding(start = 8.dp),
        )
    }
}

private const val POSITION_TICK_MS = 250L

/** How long a seek from the phone stays the base for the next press (the TV's report comes within it). */
private const val PENDING_SEEK_MS = 3_000L
