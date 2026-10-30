package app.sofabird.ui.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.SleepTimerState
import app.sofabird.ui.common.LabelText
import kotlinx.coroutines.delay

/** The sleep timer's choices in minutes ("More"). */
private val SLEEP_TIMER_MINUTES = listOf(15, 30, 45, 60, 90, 120)

/** The clock of the "… in 23 min" texts: ticks often enough for whole minutes. */
private const val TICK_MS = 15_000L

/**
 * `sleepTimer`: off, in N minutes, or at the end of what plays; "then put the PC to sleep" with `power`. The companion
 * keeps the timer, so it runs out with the phone asleep.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun SleepTimerSection(ui: RemoteUiState, tick: () -> Unit, send: (SystemCommand) -> Unit) {
    val timer = ui.state.system?.sleepTimer
    val online = ui.connection.isOnline
    var sleepPc by remember(timer?.action) { mutableStateOf(timer?.sleepsPc == true) }
    fun set(minutes: Int?, endOfItem: Boolean) {
        tick()
        send(SystemCommand.SleepTimer(minutes = minutes, endOfItem = endOfItem, sleepPc = sleepPc))
    }
    Text(
        stringResource(R.string.section_sleep_timer),
        style = MaterialTheme.typography.titleSmall,
        modifier = Modifier.padding(bottom = 6.dp),
    )
    timer?.let { SleepTimerLine(it, Modifier.padding(bottom = 6.dp)) }
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        FilterChip(
            selected = timer == null,
            onClick = { set(null, false) },
            label = { LabelText(stringResource(R.string.sleep_timer_off)) },
            enabled = online,
        )
        SLEEP_TIMER_MINUTES.forEach { m ->
            FilterChip(
                selected = false,
                onClick = { set(m, false) },
                label = { LabelText(stringResource(R.string.sleep_timer_minutes, m)) },
                enabled = online,
            )
        }
        FilterChip(
            selected = timer?.endOfItem == true,
            onClick = { set(null, true) },
            label = { LabelText(stringResource(R.string.sleep_timer_end_of_item)) },
            enabled = online,
        )
    }
    if (ui.capabilities.power) {
        SwitchRow(stringResource(R.string.sleep_timer_sleep_pc), checked = sleepPc, enabled = online) {
            sleepPc = !sleepPc
            // A running timer keeps its time and takes the new choice.
            timer?.let { t -> set(t.minutesLeft(System.currentTimeMillis())?.toInt()?.coerceAtLeast(1), t.endOfItem) }
        }
    }
}

/** "Pauses in 23 min" / "The PC sleeps at the end": under the title on the remote and in "More". */
@Composable
internal fun SleepTimerLine(timer: SleepTimerState, modifier: Modifier = Modifier) {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(timer.endsAt) {
        while (true) {
            now = System.currentTimeMillis()
            delay(TICK_MS)
        }
    }
    val minutes = timer.minutesLeft(now)
    val text = when {
        minutes == null && timer.sleepsPc -> stringResource(R.string.sleep_timer_at_end_sleep)
        minutes == null -> stringResource(R.string.sleep_timer_at_end)
        timer.sleepsPc -> stringResource(R.string.sleep_timer_left_sleep, minutes.toInt())
        else -> stringResource(R.string.sleep_timer_left, minutes.toInt())
    }
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Default.Bedtime, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(16.dp))
        Spacer(Modifier.width(6.dp))
        Text(
            text,
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.primary,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
