package app.sofabird.ui.common

import android.view.HapticFeedbackConstants
import android.view.View
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Haptic "tick" for remote buttons — short, like a keyboard tap. */
fun View.tick(enabled: Boolean = true) {
    if (!enabled) return
    performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
}

@Composable
fun rememberTick(enabled: Boolean): () -> Unit {
    val view = LocalView.current
    val current = rememberUpdatedState(enabled)
    return { view.tick(current.value) }
}

/** A firmer haptic for decisions (resume / from the beginning / play next): CONFIRM on Android 11+. */
@Composable
fun rememberConfirmHaptic(enabled: Boolean): () -> Unit {
    val view = LocalView.current
    val current = rememberUpdatedState(enabled)
    return {
        if (current.value) {
            view.performHapticFeedback(
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) HapticFeedbackConstants.CONFIRM
                else HapticFeedbackConstants.VIRTUAL_KEY,
            )
        }
    }
}

/**
 * Round remote button. Tap → [onClick]; hold → [onClick] repeats every [repeatEveryMs] after
 * [repeatAfterMs] (for ±10/±30 s). A hold that already repeated does not fire again on release.
 */
@Composable
fun RemoteButton(
    icon: ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    size: Dp = 64.dp,
    iconSize: Dp = 32.dp,
    label: String? = null,
    enabled: Boolean = true,
    filled: Boolean = false,
    repeatOnHold: Boolean = false,
    repeatAfterMs: Long = 450,
    repeatEveryMs: Long = 220,
    haptics: Boolean = true,
) {
    val scope = rememberCoroutineScope()
    val latestClick = rememberUpdatedState(onClick)
    val tick = rememberTick(haptics)
    val background = if (filled) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant
    val content = if (filled) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant
    val alpha = if (enabled) 1f else 0.38f

    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = modifier) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(size)
                .clip(CircleShape)
                .background(background.copy(alpha = if (filled) alpha else 0.6f * alpha))
                .semantics { this.contentDescription = contentDescription; role = Role.Button }
                .pointerInput(enabled, repeatOnHold) {
                    if (!enabled) return@pointerInput
                    detectTapGestures(
                        onPress = {
                            var repeated = false
                            var job: Job? = null
                            if (repeatOnHold) {
                                job = scope.launch {
                                    delay(repeatAfterMs)
                                    while (true) {
                                        repeated = true
                                        tick()
                                        latestClick.value()
                                        delay(repeatEveryMs)
                                    }
                                }
                            }
                            val released = tryAwaitRelease()
                            job?.cancel()
                            if (released && !repeated) {
                                tick()
                                latestClick.value()
                            }
                        },
                    )
                },
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = content.copy(alpha = alpha),
                modifier = Modifier.size(iconSize),
            )
            if (label != null) {
                // Inside a fixed circle: the label grows with the font scale only a little.
                LabelText(
                    text = label,
                    style = MaterialTheme.typography.labelSmall.copy(fontSize = 10.sp, fontWeight = FontWeight.Bold),
                    color = content.copy(alpha = alpha),
                    modifier = Modifier.align(Alignment.BottomCenter),
                )
            }
        }
    }
}

@Composable
fun StatusDot(color: Color, modifier: Modifier = Modifier) {
    Box(
        modifier
            .size(10.dp)
            .clip(CircleShape)
            .background(color),
    )
}

@Composable
fun DimText(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = LocalContentColor.current.copy(alpha = 0.7f),
        modifier = modifier,
    )
}
