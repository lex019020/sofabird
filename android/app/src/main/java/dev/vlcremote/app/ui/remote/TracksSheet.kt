package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.vlcremote.app.R
import dev.vlcremote.app.ui.common.LabelText
import dev.vlcremote.app.ui.common.formatDelay
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.protocol.model.TrackKind

/** Radio list of tracks: language first, description/codec dimmed; "off" entry for subtitles. */
@Composable
fun TracksSheet(kind: TrackKind, ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tracks = ui.state.tracks.of(kind)
    val current = ui.state.tracks.current(kind)
    val knowsCurrent = ui.capabilities.currentTrack || current != null
    val tick = rememberTick(ui.settings.haptics)
    val delay = if (kind == TrackKind.AUDIO) ui.state.delays.audio else ui.state.delays.subtitle

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        // One scrolling list: with a large font the delay controls scroll away with the tracks
        // instead of squeezing the track list to a couple of rows.
        LazyColumn(contentPadding = PaddingValues(bottom = 24.dp)) {
            item {
                Text(
                    stringResource(if (kind == TrackKind.AUDIO) R.string.sheet_audio_title else if (kind == TrackKind.SUBTITLE) R.string.sheet_subtitles_title else R.string.sheet_video_title),
                    style = MaterialTheme.typography.titleLarge,
                    modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                )
            }
            if (!knowsCurrent && tracks.isNotEmpty()) {
                item {
                    Text(
                        stringResource(R.string.tracks_current_unknown_hint),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(horizontal = 24.dp, vertical = 4.dp),
                    )
                }
            }
            if (tracks.isEmpty()) {
                item {
                    Text(
                        stringResource(R.string.tracks_none),
                        modifier = Modifier.padding(24.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (kind != TrackKind.AUDIO && tracks.isNotEmpty()) {
                item {
                    TrackRow(
                        title = stringResource(R.string.track_disable),
                        subtitle = null,
                        selected = current == -1,
                        onClick = { tick(); viewModel.selectTrack(kind, -1); onDismiss() },
                    )
                }
            }
            items(tracks, key = { it.id }) { track ->
                TrackRow(
                    title = track.lang ?: track.name,
                    subtitle = if (track.lang != null) listOfNotNull(track.name, track.codec).joinToString(" · ") else track.codec,
                    selected = current == track.id,
                    onClick = { tick(); viewModel.selectTrack(kind, track.id); onDismiss() },
                )
            }
            if (kind != TrackKind.VIDEO) item { Column {
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 24.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        stringResource(if (kind == TrackKind.AUDIO) R.string.delay_audio else R.string.delay_subtitles),
                        style = MaterialTheme.typography.titleSmall,
                    )
                    Text(formatDelay(delay, androidx.compose.ui.res.stringResource(dev.vlcremote.app.R.string.unit_ms)), style = MaterialTheme.typography.titleSmall)
                }
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    listOf(-500, -50, 50, 500).forEach { ms ->
                        OutlinedButton(
                            onClick = { tick(); viewModel.adjustDelay(kind, ms) },
                            modifier = Modifier.weight(1f),
                            enabled = ui.connection.isOnline,
                            contentPadding = PaddingValues(horizontal = 4.dp, vertical = 8.dp),
                        ) { LabelText(if (ms > 0) "+$ms" else "−${-ms}") }
                    }
                }
                TextButton(
                    onClick = { tick(); viewModel.resetDelay(kind) },
                    modifier = Modifier.padding(horizontal = 16.dp),
                    enabled = delay != 0.0,
                ) { LabelText(stringResource(R.string.delay_reset)) }
            } }
        }
    }
}

@Composable
private fun TrackRow(title: String, subtitle: String?, selected: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = onClick)
        Spacer(Modifier.width(8.dp))
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (!subtitle.isNullOrBlank()) {
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}
