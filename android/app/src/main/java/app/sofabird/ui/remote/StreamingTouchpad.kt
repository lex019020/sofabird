package app.sofabird.ui.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.touchpad.Touchpad

/**
 * A Moonlight stream is on the TV and the companion takes `input`: the remote is the streamed computer's touchpad and
 * keyboard (Moonlight passes the PC's input on to it). Portrait: a compact header with "End" over the touchpad and the
 * quick row; landscape: the stream's card on the left, the touchpad and the quick row on the right. While the phone's
 * keyboard is up the header and the quick row step aside, and the touchpad gives way: the remote never scrolls.
 */
@Composable
internal fun StreamingTouchpad(ui: RemoteUiState, host: String, actions: RemoteActions, landscape: Boolean) {
    val haptics = ui.settings.haptics
    val online = ui.connection.isOnline
    val onEnd = { actions.system(SystemCommand.MoonlightStop) }
    // Not `isImeVisible`: that is true until the first insets arrive (and in screenshot tests).
    val keyboardUp = WindowInsets.ime.getBottom(LocalDensity.current) > 0
    if (landscape) {
        Row(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
            Column(
                Modifier.weight(0.38f).fillMaxHeight(),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                StreamingCard(host, haptics, onEnd, narrow = true)
            }
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(0.62f).fillMaxHeight().imePadding()) {
                Touchpad(actions::input, haptics, Modifier.weight(1f).padding(top = 8.dp), enabled = online)
                if (keyboardUp) Spacer(Modifier.height(8.dp)) else BottomActions(ui, null, actions)
            }
        }
    } else {
        Column(Modifier.fillMaxSize().padding(horizontal = 16.dp).imePadding()) {
            if (keyboardUp) Spacer(Modifier.height(8.dp)) else StreamingHeader(host, haptics, onEnd)
            Touchpad(actions::input, haptics, Modifier.weight(1f), enabled = online)
            if (keyboardUp) Spacer(Modifier.height(8.dp)) else BottomActions(ui, null, actions)
            Spacer(Modifier.height(4.dp))
        }
    }
}

/** The stream in one line: the computer on the TV and "End" (back to the movie, which waits paused). */
@Composable
private fun StreamingHeader(host: String, haptics: Boolean, onEnd: () -> Unit) {
    val tick = rememberTick(haptics)
    Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
        Row(Modifier.padding(start = 16.dp, end = 12.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.Tv, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                if (host.isNotEmpty()) LabelText(host, style = MaterialTheme.typography.titleSmall)
                LabelText(
                    stringResource(R.string.moonlight_on_tv),
                    style = if (host.isEmpty()) MaterialTheme.typography.titleSmall else MaterialTheme.typography.bodySmall,
                    color = if (host.isEmpty()) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Spacer(Modifier.width(8.dp))
            Button(onClick = { tick(); onEnd() }, contentPadding = PaddingValues(horizontal = 16.dp)) {
                LabelText(stringResource(R.string.action_moonlight_end))
            }
        }
    }
}
