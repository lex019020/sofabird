package app.sofabird.ui.remote

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedIconButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.formatRate
import app.sofabird.ui.common.rememberTick

private val RATE_PRESETS = listOf(0.5, 0.75, 1.0, 1.25, 1.5, 2.0)
private val ASPECT_PRESETS = listOf("default", "16:9", "4:3", "1:1", "16:10", "2.21:1", "2.35:1", "2.39:1", "5:4")

/** "More": chapters, rate, aspect, fullscreen, companion-only system actions. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun MoreSheet(ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit, onOpenDisplays: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tick = rememberTick(ui.settings.haptics)
    val online = ui.connection.isOnline
    var showChapters by remember { mutableStateOf(false) }
    LaunchedEffect(ui.capabilities) { viewModel.loadPcInfo() }

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
                    OutlinedIconButton(
                        onClick = { tick(); viewModel.chapter((ch.index - 1).coerceAtLeast(0)) },
                        enabled = online && ch.index > 0,
                    ) {
                        Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, stringResource(R.string.action_prev))
                    }
                    Spacer(Modifier.width(12.dp))
                    Text(
                        text = ch.names?.getOrNull(ch.index)?.takeIf { it.isNotBlank() }
                            ?.let { stringResource(R.string.chapter_n_named, ch.index + 1, ch.count, it) }
                            ?: stringResource(R.string.chapter_n_of, ch.index + 1, ch.count),
                        modifier = Modifier
                            .weight(1f)
                            .clickable { showChapters = true },
                        maxLines = 3,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Spacer(Modifier.width(12.dp))
                    OutlinedIconButton(onClick = { tick(); viewModel.chapter(ch.index + 1) }, enabled = online && ch.index < ch.count - 1) {
                        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, stringResource(R.string.action_next))
                    }
                }
                OutlinedButton(onClick = { showChapters = true }, modifier = Modifier.padding(top = 4.dp)) {
                    LabelText(stringResource(R.string.chapters_all))
                }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))

            // Rate
            SectionTitle(stringResource(R.string.section_rate, formatRate(ui.state.playback.rate)))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                RATE_PRESETS.forEach { r ->
                    FilterChip(
                        selected = kotlin.math.abs(ui.state.playback.rate - r) < 0.03,
                        onClick = { tick(); viewModel.rate(r) },
                        label = { LabelText(formatRate(r)) },
                        enabled = online,
                    )
                }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))

            // Aspect
            SectionTitle(stringResource(R.string.section_aspect))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                ASPECT_PRESETS.forEach { a ->
                    FilterChip(
                        selected = ui.state.video.aspect == a || (a == "default" && ui.state.video.aspect.isBlank()),
                        onClick = { tick(); viewModel.aspect(a) },
                        label = { LabelText(if (a == "default") stringResource(R.string.aspect_default) else a) },
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

            // Companion-only sections, gated by capabilities.
            val caps = ui.capabilities
            if (ui.isCompanion || caps.launchVlc) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionTitle(stringResource(R.string.section_pc))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = { tick(); viewModel.launchVlc() },
                        enabled = online && caps.launchVlc && !ui.state.vlc.running,
                    ) {
                        LabelText(stringResource(R.string.action_launch_vlc))
                    }
                    OutlinedButton(onClick = { tick(); viewModel.quitVlc(); onDismiss() }, enabled = online && ui.state.vlc.running) {
                        LabelText(stringResource(R.string.action_quit_vlc))
                    }
                }
            }
            if (caps.moonlight) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                MoonlightSection(
                    host = ui.state.system?.moonlightHost,
                    streaming = ui.state.system?.moonlightStreaming == true,
                    enabled = online,
                    onStart = { tick(); viewModel.sendSystem(SystemCommand.MoonlightStart()); onDismiss() },
                    onStop = { tick(); viewModel.sendSystem(SystemCommand.MoonlightStop); onDismiss() },
                )
            }
            if (caps.systemVolume) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SystemVolumeSection(ui, viewModel)
            }
            if (caps.audioDevices) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                AudioDeviceSection(ui, viewModel)
            }
            if (ui.showDisplayChoice) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                DisplayEntry(ui, onOpenDisplays)
            }
            if (caps.power) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                PowerSection(ui, viewModel, onDismiss)
            }
            if (!ui.isCompanion && online) {
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                OutlinedButton(onClick = { tick(); viewModel.quitVlc(); onDismiss() }, enabled = online) {
                    LabelText(stringResource(R.string.action_quit_vlc))
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

/** `systemVolume`: Windows master volume, separate from VLC's own. */
@Composable
private fun SystemVolumeSection(ui: RemoteUiState, viewModel: RemoteViewModel) {
    val system = ui.state.system
    val online = ui.connection.isOnline
    val tick = rememberTick(ui.settings.haptics)
    var dragging by remember { mutableStateOf<Float?>(null) }
    SectionTitle(stringResource(R.string.section_system_volume))
    Row(verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { tick(); viewModel.toggleSystemMute() }, enabled = online) {
            Icon(
                if (system?.muted == true) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                contentDescription = stringResource(R.string.action_mute),
            )
        }
        Slider(
            value = dragging ?: (system?.volume ?: 0).toFloat(),
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { tick(); viewModel.setSystemVolume(it.toInt()) }
                dragging = null
            },
            valueRange = 0f..100f,
            enabled = online && system?.volume != null,
            modifier = Modifier.weight(1f),
        )
        LabelText(
            "${(dragging ?: (system?.volume ?: 0).toFloat()).toInt()} %",
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.widthIn(min = 56.dp),
            textAlign = TextAlign.End,
        )
    }
}

/** `audioDevices`: radio list from `getAudioDevices`; "" = Windows default device. */
@Composable
private fun AudioDeviceSection(ui: RemoteUiState, viewModel: RemoteViewModel) {
    val tick = rememberTick(ui.settings.haptics)
    val online = ui.connection.isOnline
    val currentId = ui.state.system?.audioDevice
    val devices = ui.audioDevices
    val hasDefault = devices.any { it.id.isEmpty() }
    val anyCurrent = devices.any { it.current }
    Row(verticalAlignment = Alignment.CenterVertically) {
        SectionTitle(stringResource(R.string.section_audio_device))
        Spacer(Modifier.weight(1f))
        if (ui.pcInfoLoading) CircularProgressIndicator(Modifier.width(16.dp), strokeWidth = 2.dp)
        IconButton(onClick = { viewModel.loadPcInfo() }, enabled = online) { Icon(Icons.Default.Refresh, stringResource(R.string.refresh)) }
    }
    if (devices.isEmpty() && !ui.pcInfoLoading) {
        Text(stringResource(R.string.audio_devices_none), color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if (!hasDefault) {
        RadioRow(
            title = stringResource(R.string.audio_device_default),
            selected = !anyCurrent && currentId.isNullOrEmpty(),
            enabled = online,
            onClick = { tick(); viewModel.selectAudioDevice("") },
        )
    }
    devices.forEach { device ->
        RadioRow(
            title = if (device.id.isEmpty()) stringResource(R.string.audio_device_default) else device.name,
            selected = if (anyCurrent) device.current else currentId == device.id,
            enabled = online,
            onClick = { tick(); viewModel.selectAudioDevice(device.id) },
        )
    }
}

/** `windowControl`: where the VLC window is / opens; the choice itself lives in [DisplaySheet]. */
@Composable
private fun DisplayEntry(ui: RemoteUiState, onOpen: () -> Unit) {
    val shown = ui.displayChoice.chipDisplay
    SectionTitle(stringResource(R.string.section_display))
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .clickable(onClick = onOpen)
            .padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(displayIcon(shown), contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(stringResource(R.string.sheet_display_title), style = MaterialTheme.typography.bodyLarge)
            Text(
                shown?.let { displayLabel(it) } ?: stringResource(R.string.display_choose_hint),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null)
    }
}

/**
 * `moonlight`: show another computer's screen on the TV through Moonlight on the PC (paired with a Sunshine host),
 * and end that stream. Without a paired computer only a hint is shown.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun MoonlightSection(host: String?, streaming: Boolean, enabled: Boolean, onStart: () -> Unit, onStop: () -> Unit) {
    SectionTitle(stringResource(R.string.section_moonlight))
    Text(
        if (host == null) stringResource(R.string.moonlight_no_host) else stringResource(R.string.moonlight_from, host),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 8.dp)) {
        if (streaming) {
            Button(onClick = onStop, enabled = enabled) { LabelText(stringResource(R.string.action_moonlight_stop)) }
        } else {
            OutlinedButton(onClick = onStart, enabled = enabled && host != null) {
                LabelText(stringResource(R.string.action_moonlight_start))
            }
        }
    }
}

/** `power`: keep-awake switch, display off, sleep (behind a confirmation). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PowerSection(ui: RemoteUiState, viewModel: RemoteViewModel, onDismiss: () -> Unit) {
    val tick = rememberTick(ui.settings.haptics)
    val online = ui.connection.isOnline
    var confirmSleep by remember { mutableStateOf(false) }
    SectionTitle(stringResource(R.string.section_power))
    val keepAwake = ui.state.system?.keepAwake == true
    SwitchRow(
        title = stringResource(R.string.keep_awake),
        checked = keepAwake,
        enabled = online,
        onToggle = { tick(); viewModel.setKeepAwake(!keepAwake) },
    )
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 4.dp)) {
        OutlinedButton(onClick = { tick(); viewModel.displayPower(false) }, enabled = online) {
            LabelText(stringResource(R.string.action_display_off))
        }
        OutlinedButton(onClick = { tick(); confirmSleep = true }, enabled = online) {
            LabelText(stringResource(R.string.action_sleep_pc))
        }
    }
    if (confirmSleep) {
        AlertDialog(
            onDismissRequest = { confirmSleep = false },
            title = { Text(stringResource(R.string.action_sleep_pc)) },
            text = { Text(stringResource(R.string.sleep_pc_confirm, ui.hostName ?: "")) },
            confirmButton = {
                TextButton(onClick = { confirmSleep = false; viewModel.sleepPc(); onDismiss() }) {
                    Text(stringResource(R.string.action_sleep_pc))
                }
            },
            dismissButton = { TextButton(onClick = { confirmSleep = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
private fun RadioRow(title: String, selected: Boolean, enabled: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled, onClick = onClick)
            .padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = onClick, enabled = enabled)
        Spacer(Modifier.width(8.dp))
        Text(title, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
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
            OutlinedButton(onClick = onBack) { LabelText(stringResource(R.string.back)) }
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
        Text(title, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f).padding(end = 12.dp))
        Switch(checked = checked, onCheckedChange = { onToggle() }, enabled = enabled)
    }
}
