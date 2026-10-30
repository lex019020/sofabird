package app.sofabird.ui.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.backend.YouTubeAction
import app.sofabird.protocol.model.YouTubeState
import app.sofabird.protocol.model.youTubeQualityLabel
import app.sofabird.ui.common.LabelText

/** The quality chips' "Auto" (YouTube picks by the connection). */
private const val AUTO = "auto"

/**
 * YouTube on the TV: its subtitles (off, or one of the video's tracks) and its quality (Auto or a fixed one), from what
 * the TV extension reports (`youtube.captions`/`qualities`).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun YouTubeVideoSheet(
    youtube: YouTubeState,
    enabled: Boolean,
    tick: () -> Unit,
    send: (SystemCommand) -> Unit,
    onDismiss: () -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Column(
            Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp)
                .padding(bottom = 32.dp),
        ) {
            Text(stringResource(R.string.youtube_video_title), style = MaterialTheme.typography.titleLarge)
            if (youtube.captions.isNotEmpty()) {
                Text(
                    stringResource(R.string.youtube_captions_title),
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.padding(top = 16.dp, bottom = 6.dp),
                )
                RadioRow(stringResource(R.string.youtube_captions_off), selected = youtube.caption == null, enabled = enabled) {
                    tick()
                    send(SystemCommand.YouTube(YouTubeAction.CAPTIONS, value = ""))
                }
                youtube.captions.forEach { caption ->
                    RadioRow(caption.name, selected = youtube.caption == caption.code, enabled = enabled) {
                        tick()
                        send(SystemCommand.YouTube(YouTubeAction.CAPTIONS, value = caption.code))
                    }
                }
            }
            val levels = youtube.qualities.filter { it != AUTO }
            if (levels.isNotEmpty()) {
                if (youtube.captions.isNotEmpty()) HorizontalDivider(Modifier.padding(top = 12.dp))
                Text(
                    stringResource(R.string.youtube_quality_title),
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.padding(top = 16.dp, bottom = 6.dp),
                )
                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    if (AUTO in youtube.qualities) {
                        FilterChip(
                            selected = false,
                            onClick = { tick(); send(SystemCommand.YouTube(YouTubeAction.QUALITY, value = AUTO)) },
                            label = { LabelText(stringResource(R.string.youtube_quality_auto)) },
                            enabled = enabled,
                        )
                    }
                    levels.forEach { level ->
                        FilterChip(
                            selected = youtube.quality == level,
                            onClick = { tick(); send(SystemCommand.YouTube(YouTubeAction.QUALITY, value = level)) },
                            label = { LabelText(youTubeQualityLabel(level) ?: level) },
                            enabled = enabled,
                        )
                    }
                }
            }
        }
    }
}

/** The "Video" tile's value: the subtitles' language (or off) and the quality — "EN · 1080p". */
@Composable
internal fun youTubeVideoSummary(youtube: YouTubeState): String? {
    val parts = buildList {
        if (youtube.captions.isNotEmpty()) add(youtube.caption?.uppercase() ?: stringResource(R.string.youtube_captions_off))
        youtube.quality?.let { q -> youTubeQualityLabel(q)?.let(::add) }
    }
    return parts.takeIf { it.isNotEmpty() }?.joinToString(" · ")
}
