package dev.vlcremote.app.ui.remote

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.vlcremote.app.R
import dev.vlcremote.app.ui.common.formatRate
import dev.vlcremote.app.ui.common.rememberTick
import dev.vlcremote.protocol.backend.SystemCommand

private val RATE_PRESETS = listOf(0.5, 0.75, 1.0, 1.25, 1.5, 2.0)
private val ASPECT_PRESETS = listOf("default", "16:9", "4:3", "1:1", "16:10", "2.21:1", "2.35:1", "2.39:1", "5:4")

/** "More": chapters, rate, aspect, fullscreen, companion-only system actions. */
@Composable
fun MoreSheet(ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tick = rememberTick(ui.settings.haptics)
    val online = ui.connection.isOnline
    var showChapters by remember { mutableStateOf(false) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        if (showChapters) {
            ChaptersList(ui, viewModel, onBack = { showChapters = false }, onPicked = onDismiss)
            return@ModalBottomSheet
        }
        Column(
            Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp)
                .padding(bottom = 32.dp),
        ) {
            Text(stringResource(R.string.sheet_more_title), style = MaterialTheme.typography.titleLarge)
            Spacer(Modifier.height(12.dp))

            // Chapters
            val ch = ui.state.chapters
            SectionTitle(stringResource(R.string.section_chapters))
            if (ch.count == 0) {
                Text(stringResource(R.string.chapters_none), color = MaterialTheme.colorScheme.onSurfaceVariant)
            } else {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedButton(onClick = { tick(); viewModel.chapter((ch.index - 1).coerceAtLeast(0)) }, enabled = online && ch.index > 0) { Text("◀") }
                    Spacer(Modifier.width(8.dp))
                    Text(
                        text = ch.names?.getOrNull(ch.index)?.takeIf { it.isNotBlank() }
                            ?.let { stringResource(R.string.chapter_n_named, ch.index + 1, ch.count, it) }
                            ?: stringResource(R.string.chapter_n_of, ch.index + 1, ch.count),
                        modifier = Modifier
                            .weight(1f)
                            .clickable { showChapters = true },
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Spacer(Modifier.width(8.dp))
                    OutlinedButton(onClick = { tick(); viewModel.chapter(ch.index + 1) }, enabled = online && ch.index < ch.count - 1) { Text("▶") }
                }
                OutlinedButton(onClick = { showChapters = true }, modifier = Modifier.padding(top = 4.dp)) {
                    Text(stringResource(R.string.chapters_all))
                }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))

            // Rate
            SectionTitle(stringResource(R.string.section_rate, formatRate(ui.state.playback.rate)))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.horizontalScroll(rememberScrollState())) {
                RATE_PRESETS.forEach { r ->
                    FilterChip(
                        selected = kotlin.math.abs(ui.state.playback.rate - r) < 0.03,
                        onClick = { tick(); viewModel.rate(r) },
                        label = { Text(formatRate(r)) },
                        enabled = online,
                    )
                }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))

            // Aspect
            SectionTitle(stringResource(R.string.section_aspect))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.horizontalScroll(rememberScrollState())) {
                ASPECT_PRESETS.forEach { a ->
                    FilterChip(
                        selected = ui.state.video.aspect == a || (a == "default" && ui.state.video.aspect.isBlank()),
                        onClick = { tick(); viewModel.aspect(a) },
                        label = { Text(if (a == "default") stringResource(R.string.aspect_default) else a) },
                        enabled = online,
                    )
                }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))

            SwitchRow(
                title = stringResource(R.string.fullscreen),
                checked = ui.state.video.fullscreen,
                enabled = online && !ui.state.isStopped,
                onToggle = { tick(); viewModel.toggleFullscreen() },
            )

            // Companion-only
            val caps = ui.capabilities
            if (ui.isCompanion || caps.launchVlc || caps.power || caps.windowControl) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionTitle(stringResource(R.string.section_pc))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { tick(); viewModel.launchVlc() }, enabled = online && caps.launchVlc && !ui.state.vlc.running) {
                        Text(stringResource(R.string.action_launch_vlc))
                    }
                    OutlinedButton(onClick = { tick(); viewModel.quitVlc(); onDismiss() }, enabled = online && ui.state.vlc.running) {
                        Text(stringResource(R.string.action_quit_vlc))
                    }
                }
                if (caps.power) {
                    Spacer(Modifier.height(8.dp))
                    SwitchRow(
                        title = stringResource(R.string.keep_awake),
                        checked = ui.state.system?.keepAwake == true,
                        enabled = online,
                        onToggle = { tick(); viewModel.sendSystem(SystemCommand.KeepAwake(ui.state.system?.keepAwake != true)) },
                    )
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick = { tick(); viewModel.sendSystem(SystemCommand.DisplayPower(false)) }, enabled = online) {
                            Text(stringResource(R.string.action_display_off))
                        }
                        OutlinedButton(onClick = { tick(); viewModel.sendSystem(SystemCommand.Sleep); onDismiss() }, enabled = online) {
                            Text(stringResource(R.string.action_sleep_pc))
                        }
                    }
                }
            } else if (!ui.isCompanion && online) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { tick(); viewModel.quitVlc(); onDismiss() }, enabled = online) {
                        Text(stringResource(R.string.action_quit_vlc))
                    }
                }
                Text(
                    stringResource(R.string.needs_companion_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 8.dp),
                )
            }
        }
    }
}

@Composable
private fun ChaptersList(ui: RemoteUiState, viewModel: RemoteViewModel, onBack: () -> Unit, onPicked: () -> Unit) {
    val ch = ui.state.chapters
    val tick = rememberTick(ui.settings.haptics)
    val listState = rememberLazyListState()
    LaunchedEffect(ch.index) { if (ch.index >= 0) listState.scrollToItem(ch.index) }
    Column(Modifier.padding(bottom = 24.dp)) {
        Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedButton(onClick = onBack) { Text(stringResource(R.string.back)) }
            Spacer(Modifier.width(12.dp))
            Text(stringResource(R.string.section_chapters), style = MaterialTheme.typography.titleLarge)
        }
        LazyColumn(state = listState) {
            itemsIndexed(List(ch.count) { it }) { i, _ ->
                val name = ch.names?.getOrNull(i)?.takeIf { it.isNotBlank() }
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clickable { tick(); viewModel.chapter(i); onPicked() }
                        .padding(horizontal = 24.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        "${i + 1}.",
                        style = MaterialTheme.typography.bodyLarge,
                        color = if (i == ch.index) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.width(40.dp),
                    )
                    Text(
                        name ?: stringResource(R.string.chapter_unnamed, i + 1),
                        style = MaterialTheme.typography.bodyLarge,
                        color = if (i == ch.index) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(text, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(bottom = 6.dp))
}

@Composable
private fun SwitchRow(title: String, checked: Boolean, enabled: Boolean, onToggle: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled, onClick = onToggle)
            .padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(title, style = MaterialTheme.typography.bodyLarge)
        Switch(checked = checked, onCheckedChange = { onToggle() }, enabled = enabled)
    }
}
