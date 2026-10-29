package app.sofabird.ui.history

import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SmartDisplay
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sofabird.R
import app.sofabird.data.history.WatchPrompts
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.protocol.model.ResumeChoice
import app.sofabird.protocol.model.ResumePrompt
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.displayNameFromUri
import app.sofabird.ui.common.formatTime
import app.sofabird.ui.common.rememberConfirmHaptic
import app.sofabird.ui.common.withNameBreaks
import org.koin.compose.koinInject

/** "1:23:45 of 2:58:25" (or just the position when the length is unknown). */
@Composable
fun positionOfLength(position: Long, length: Long): String =
    if (length > 0) stringResource(R.string.history_position_of, formatTime(position), formatTime(length))
    else formatTime(position)

/** Thin progress bar for history rows and cards. */
@Composable
fun WatchProgressBar(fraction: Float, modifier: Modifier = Modifier, height: Dp = 4.dp) {
    LinearProgressIndicator(
        progress = { fraction.coerceIn(0f, 1f) },
        modifier = modifier
            .height(height)
            .clip(RoundedCornerShape(height / 2)),
        drawStopIndicator = {},
        gapSize = 0.dp,
    )
}

// ------------------------------------------------------------------ resume prompt (global)

/**
 * Shows the resume dialog whenever the PC waits for an answer, over any screen of the app. The
 * dialog disappears when the PC drops `state.resume` (another phone answered, Play pressed in VLC).
 */
@Composable
fun ResumePromptHost() {
    val prompts: WatchPrompts = koinInject()
    val settings: SettingsRepository = koinInject()
    val prompt by prompts.resume.collectAsStateWithLifecycle()
    val prefs by settings.settings.collectAsStateWithLifecycle()
    val haptic = rememberConfirmHaptic(prefs.haptics)
    prompt?.let { p ->
        ResumePromptDialog(p) { choice ->
            haptic()
            prompts.answer(choice)
        }
    }
}

@Composable
fun ResumePromptDialog(prompt: ResumePrompt, onChoice: (ResumeChoice) -> Unit) {
    // Modal on purpose: VLC waits paused for the answer; back / outside taps do not dismiss it.
    Dialog(
        onDismissRequest = {},
        properties = DialogProperties(dismissOnBackPress = false, dismissOnClickOutside = false, usePlatformDefaultWidth = false),
    ) {
        Surface(
            shape = RoundedCornerShape(28.dp),
            tonalElevation = 6.dp,
            modifier = Modifier
                .padding(horizontal = 20.dp)
                .widthIn(max = 520.dp)
                .fillMaxWidth(),
        ) {
            Column(Modifier.padding(24.dp)) {
                Text(
                    stringResource(R.string.resume_title),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.primary,
                )
                Spacer(Modifier.height(8.dp))
                Text(
                    (prompt.title?.takeIf { it.isNotBlank() } ?: displayNameFromUri(prompt.uri)).withNameBreaks(),
                    style = MaterialTheme.typography.headlineSmall,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
                Spacer(Modifier.height(16.dp))
                if (prompt.length > 0) {
                    WatchProgressBar(prompt.progress.toFloat(), Modifier.fillMaxWidth(), height = 6.dp)
                    Spacer(Modifier.height(8.dp))
                }
                Text(
                    if (prompt.length > 0) {
                        stringResource(R.string.resume_stopped_at, formatTime(prompt.position), formatTime(prompt.length))
                    } else {
                        stringResource(R.string.resume_stopped_at_no_length, formatTime(prompt.position))
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(24.dp))
                // Large, full-width buttons; labels wrap instead of being cut at big font sizes.
                Button(
                    onClick = { onChoice(ResumeChoice.RESUME) },
                    contentPadding = PaddingValues(horizontal = 20.dp, vertical = 14.dp),
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 64.dp),
                ) {
                    Icon(Icons.Default.PlayArrow, contentDescription = null)
                    Spacer(Modifier.width(10.dp))
                    Text(
                        stringResource(R.string.resume_continue_from, formatTime(prompt.position)),
                        style = MaterialTheme.typography.titleMedium,
                        textAlign = TextAlign.Center,
                    )
                }
                Spacer(Modifier.height(12.dp))
                OutlinedButton(
                    onClick = { onChoice(ResumeChoice.RESTART) },
                    contentPadding = PaddingValues(horizontal = 20.dp, vertical = 14.dp),
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 64.dp),
                ) {
                    Icon(Icons.Default.Replay, contentDescription = null)
                    Spacer(Modifier.width(10.dp))
                    Text(
                        stringResource(R.string.resume_from_start),
                        style = MaterialTheme.typography.titleMedium,
                        textAlign = TextAlign.Center,
                    )
                }
                Spacer(Modifier.height(16.dp))
                Text(
                    stringResource(R.string.resume_waiting_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

// ------------------------------------------------------------------ next episode (Remote screen)

/** "Next episode: <title>" with "Play" (`cmd open`) and "Dismiss" (`cmd dismissNext`). Never plays by itself. */
@Composable
fun NextEpisodeCard(next: NextEpisode, haptics: Boolean, onPlay: () -> Unit, onDismiss: () -> Unit, modifier: Modifier = Modifier) {
    val haptic = rememberConfirmHaptic(haptics)
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = 8.dp),
    ) {
        Column(Modifier.padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Default.SkipNext,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSecondaryContainer,
                    modifier = Modifier.size(18.dp),
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    stringResource(R.string.next_episode_label),
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onSecondaryContainer,
                )
            }
            Spacer(Modifier.height(4.dp))
            Text(
                (next.title?.takeIf { it.isNotBlank() } ?: displayNameFromUri(next.uri)).withNameBreaks(),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSecondaryContainer,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Row(
                Modifier
                    .fillMaxWidth()
                    .padding(top = 4.dp),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextButton(onClick = { haptic(); onDismiss() }) { LabelText(stringResource(R.string.next_episode_dismiss)) }
                Spacer(Modifier.width(8.dp))
                Button(onClick = { haptic(); onPlay() }) {
                    Icon(Icons.Default.PlayArrow, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    LabelText(stringResource(R.string.next_episode_play))
                }
            }
        }
    }
}

// ------------------------------------------------------------------ Library: "Continue watching" row

/** Header + horizontal cards. Tap → play (the PC asks about resuming); long press → menu. */
@Composable
fun ContinueWatchingRow(
    cards: List<ContinueCard>,
    onOpen: (ContinueCard) -> Unit,
    onMenu: (ContinueCard) -> Unit,
    onHistory: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier.fillMaxWidth()) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(start = 16.dp, end = 4.dp, top = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
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
        // Cards grow with the font size, so "1:23:45 of 2:58:25" and two title lines still fit.
        val fontScale = LocalDensity.current.fontScale.coerceIn(1f, 1.6f)
        val cardWidth = (172f * fontScale).dp
        LazyRow(
            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            items(cards, key = { it.uri }) { card ->
                ContinueCardView(card, Modifier.width(cardWidth), onClick = { onOpen(card) }, onLongClick = { onMenu(card) })
            }
        }
    }
}

@Composable
private fun ContinueCardView(card: ContinueCard, modifier: Modifier, onClick: () -> Unit, onLongClick: () -> Unit) {
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = modifier,
    ) {
        Column(
            Modifier
                .combinedClickable(onClick = onClick, onLongClick = onLongClick)
                .padding(12.dp),
        ) {
            Text(
                card.title.withNameBreaks(),
                style = MaterialTheme.typography.titleSmall,
                maxLines = 2,
                minLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(10.dp))
            if (card.isNext) {
                Surface(
                    color = MaterialTheme.colorScheme.tertiaryContainer,
                    contentColor = MaterialTheme.colorScheme.onTertiaryContainer,
                    shape = RoundedCornerShape(6.dp),
                ) {
                    Row(Modifier.padding(horizontal = 6.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.SkipNext, contentDescription = null, modifier = Modifier.size(14.dp))
                        Spacer(Modifier.width(4.dp))
                        Text(
                            stringResource(R.string.history_next_badge),
                            style = MaterialTheme.typography.labelSmall,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                // Same height as the progress variant, so the row stays even.
                Spacer(Modifier.height(4.dp))
            } else {
                WatchProgressBar(card.fraction, Modifier.fillMaxWidth())
                Spacer(Modifier.height(6.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (card.item.isYouTube) {
                        // A YouTube video (it opens on the TV in the browser).
                        Icon(
                            Icons.Default.SmartDisplay,
                            contentDescription = stringResource(R.string.history_youtube),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(14.dp),
                        )
                        Spacer(Modifier.width(4.dp))
                    }
                    Text(
                        positionOfLength(card.position, card.length),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        }
    }
}

/** Check mark for a watched file / history row. */
@Composable
fun WatchedMark(modifier: Modifier = Modifier) {
    Icon(
        Icons.Default.CheckCircle,
        contentDescription = stringResource(R.string.history_watched),
        tint = MaterialTheme.colorScheme.primary,
        modifier = modifier.size(20.dp),
    )
}
