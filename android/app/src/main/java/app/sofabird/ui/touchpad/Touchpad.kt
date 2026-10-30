package app.sofabird.ui.touchpad

import android.view.HapticFeedbackConstants
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.KeyboardHide
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material3.FilledIconToggleButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedIconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.AwaitPointerEventScope
import androidx.compose.ui.input.pointer.PointerEvent
import androidx.compose.ui.input.pointer.PointerInputScope
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.layout.Placeable
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.backend.MouseButton
import app.sofabird.protocol.backend.PcInput
import app.sofabird.protocol.backend.PcKey
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.tick

/**
 * The phone as the PC's touchpad and keyboard (`input` capability): the pad (one finger moves the pointer, a tap clicks,
 * two fingers scroll or right-click with a tap, a hold drags), Left and Right buttons, and a keyboard whose text goes to
 * the PC as it is typed, with a row of keys a phone keyboard lacks. It takes the height it is given (the pad gives way).
 *
 * [keyboardOpen]: the keyboard is shown from the start (screenshots).
 */
@Composable
fun Touchpad(
    onInput: (PcInput) -> Unit,
    haptics: Boolean,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    keyboardOpen: Boolean = false,
) {
    var keyboard by rememberSaveable { mutableStateOf(keyboardOpen) }
    // Only a tap on the toggle opens the phone's keyboard; a keyboard shown from the start waits for a tap on the field.
    var focusField by remember { mutableStateOf(false) }
    val tick = rememberTick(haptics)
    val softKeyboard = LocalSoftwareKeyboardController.current
    Column(modifier) {
        TouchSurface(onInput, haptics, enabled, Modifier.weight(1f).fillMaxWidth())
        Row(
            Modifier
                .fillMaxWidth()
                .padding(top = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MouseButtonKey(R.string.touchpad_left, enabled, Modifier.weight(1f)) { tick(); onInput(PcInput.Click(MouseButton.LEFT)) }
            MouseButtonKey(R.string.touchpad_right, enabled, Modifier.weight(1f)) { tick(); onInput(PcInput.Click(MouseButton.RIGHT)) }
            FilledIconToggleButton(
                checked = keyboard,
                onCheckedChange = {
                    tick()
                    keyboard = it
                    focusField = it
                    if (!it) softKeyboard?.hide()
                },
                enabled = enabled,
            ) {
                Icon(
                    if (keyboard) Icons.Default.KeyboardHide else Icons.Default.Keyboard,
                    contentDescription = stringResource(R.string.touchpad_keyboard),
                )
            }
        }
        if (keyboard) KeyboardPanel(onInput, haptics, enabled, focusField) { focusField = false }
    }
}

@Composable
private fun MouseButtonKey(label: Int, enabled: Boolean, modifier: Modifier, onClick: () -> Unit) {
    FilledTonalButton(onClick = onClick, enabled = enabled, modifier = modifier, contentPadding = PaddingValues(horizontal = 8.dp)) {
        LabelText(stringResource(label))
    }
}

/** The pad itself, with how to use it in the middle (as much of that as fits). */
@Composable
private fun TouchSurface(onInput: (PcInput) -> Unit, haptics: Boolean, enabled: Boolean, modifier: Modifier) {
    val view = LocalView.current
    val input by rememberUpdatedState(onInput)
    val haptic by rememberUpdatedState(haptics)
    val shape = RoundedCornerShape(20.dp)
    val description = stringResource(R.string.touchpad_title)
    Box(
        modifier
            .clip(shape)
            .background(MaterialTheme.colorScheme.surfaceContainerHigh)
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, shape)
            .semantics { contentDescription = description }
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                detectTouchpad(
                    emit = { input(it) },
                    onTap = { view.tick(haptic) },
                    onHold = { if (haptic) view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS) },
                )
            }
            .padding(16.dp)
            .alpha(if (enabled) 1f else 0.38f),
        contentAlignment = Alignment.Center,
    ) {
        FittingHint {
            Icon(
                Icons.Default.TouchApp,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f),
                modifier = Modifier.size(32.dp),
            )
            stringResource(R.string.touchpad_hint).lines().forEach { line ->
                Text(
                    line,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                )
            }
        }
    }
}

/**
 * An icon over lines of a hint, centred: as much as fits the height — the icon goes first, then the last lines (the
 * first ones matter most). A line is never cut in half.
 */
@Composable
private fun FittingHint(content: @Composable () -> Unit) {
    Layout(content) { measurables, constraints ->
        val gap = 4.dp.roundToPx()
        val loose = constraints.copy(minWidth = 0, minHeight = 0, maxHeight = Constraints.Infinity)
        val all = measurables.map { it.measure(loose) }
        fun heightOf(list: List<Placeable>) = list.sumOf { it.height } + gap * (list.size - 1).coerceAtLeast(0)
        var shown = all
        if (heightOf(shown) > constraints.maxHeight) shown = all.drop(1) // without the icon
        while (shown.isNotEmpty() && heightOf(shown) > constraints.maxHeight) shown = shown.dropLast(1)
        val width = shown.maxOfOrNull { it.width } ?: 0
        layout(width, heightOf(shown)) {
            var y = 0
            shown.forEach {
                it.place((width - it.width) / 2, y)
                y += it.height + gap
            }
        }
    }
}

/**
 * The keyboard: a field whose text is typed on the PC as it changes ([KeyboardStream]) and the keys a phone keyboard
 * lacks. [focus]: open the phone's keyboard now ([onFocused] once done).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun KeyboardPanel(onInput: (PcInput) -> Unit, haptics: Boolean, enabled: Boolean, focus: Boolean, onFocused: () -> Unit) {
    val stream = remember { KeyboardStream() }
    var value by remember { mutableStateOf(stream.fieldValue()) }
    val focusRequester = remember { FocusRequester() }
    val tick = rememberTick(haptics)
    LaunchedEffect(focus) {
        if (focus) {
            focusRequester.requestFocus()
            onFocused()
        }
    }
    val press: (PcKey) -> Unit = { key ->
        tick()
        stream.key(key).forEach(onInput)
        value = stream.fieldValue()
    }
    OutlinedTextField(
        value = value,
        onValueChange = { changed ->
            stream.edit(changed.text).forEach(onInput)
            // The IME's own value while it matches (it keeps the composition); the field's text when it starts over.
            value = if (changed.text == stream.field) changed else stream.fieldValue()
        },
        label = { LabelText(stringResource(R.string.keyboard_field_label)) },
        // Not single-line: the IME's Enter then types a new line, which goes to the PC as Enter.
        singleLine = false,
        maxLines = 1,
        keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None, autoCorrectEnabled = false),
        enabled = enabled,
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 8.dp)
            .focusRequester(focusRequester),
    )
    FlowRow(
        Modifier
            .fillMaxWidth()
            .padding(top = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        TextKey(R.string.key_escape, enabled) { press(PcKey.ESCAPE) }
        TextKey(R.string.key_tab, enabled) { press(PcKey.TAB) }
        TextKey(R.string.key_win, enabled) { press(PcKey.WIN) }
        TextKey(R.string.key_backspace, enabled) { press(PcKey.BACKSPACE) }
        TextKey(R.string.key_enter, enabled) { press(PcKey.ENTER) }
        IconKey(Icons.AutoMirrored.Filled.KeyboardArrowLeft, R.string.key_left, enabled) { press(PcKey.LEFT) }
        IconKey(Icons.Default.KeyboardArrowUp, R.string.key_up, enabled) { press(PcKey.UP) }
        IconKey(Icons.Default.KeyboardArrowDown, R.string.key_down, enabled) { press(PcKey.DOWN) }
        IconKey(Icons.AutoMirrored.Filled.KeyboardArrowRight, R.string.key_right, enabled) { press(PcKey.RIGHT) }
    }
}

private fun KeyboardStream.fieldValue() = TextFieldValue(field, TextRange(field.length))

@Composable
private fun TextKey(label: Int, enabled: Boolean, onClick: () -> Unit) {
    OutlinedButton(onClick = onClick, enabled = enabled, contentPadding = PaddingValues(horizontal = 14.dp)) {
        LabelText(stringResource(label))
    }
}

@Composable
private fun IconKey(icon: ImageVector, description: Int, enabled: Boolean, onClick: () -> Unit) {
    OutlinedIconButton(onClick = onClick, enabled = enabled) {
        Icon(icon, contentDescription = stringResource(description))
    }
}

// ------------------------------------------------------------------ gestures

/** How a touch on the pad began: released, moved, or held still for the long-press time. */
private enum class Stroke { TAP, MOVE, SCROLL, DRAG, HOLD }

/** One pointer event: the fingers still down, their average movement (px) and the time (ms) since the last event. */
private class Motion(val fingers: Int, val delta: Offset, val dtMs: Long)

/**
 * One finger moves the pointer; a tap (released before the long-press time, within the touch slop) clicks — with two
 * fingers the right button, with three the middle one; two fingers scroll; a finger held still for the long-press time
 * presses the left button ([onHold]), moves then drag, and lifting it releases the button.
 */
private suspend fun PointerInputScope.detectTouchpad(emit: (PcInput) -> Unit, onTap: () -> Unit, onHold: () -> Unit) {
    val pointer = PointerMotion()
    val scroll = ScrollMotion()
    awaitEachGesture {
        awaitFirstDown(requireUnconsumed = false).consume()
        pointer.reset()
        var fingers = 1
        var travel = Offset.Zero
        val began = withTimeoutOrNull(viewConfiguration.longPressTimeoutMillis) {
            awaitMotion { motion ->
                fingers = maxOf(fingers, motion.fingers)
                travel += motion.delta
                when {
                    motion.fingers == 0 -> Stroke.TAP
                    travel.getDistance() > viewConfiguration.touchSlop -> if (fingers > 1) Stroke.SCROLL else Stroke.MOVE
                    else -> null
                }
            }
        } ?: if (fingers > 1) Stroke.HOLD else Stroke.DRAG
        when (began) {
            Stroke.TAP -> {
                onTap()
                emit(PcInput.Click(tapButton(fingers)))
                return@awaitEachGesture
            }
            Stroke.DRAG -> {
                onHold()
                emit(PcInput.Press(MouseButton.LEFT))
            }
            Stroke.SCROLL -> scroll.start(travel.x, travel.y)
            Stroke.HOLD -> scroll.start(0f, 0f) // two fingers held still: they may scroll yet, but it is no click
            Stroke.MOVE -> Unit
        }
        var scrolling = began == Stroke.SCROLL || began == Stroke.HOLD
        awaitMotion { motion ->
            if (motion.fingers == 0) return@awaitMotion Unit
            if (began == Stroke.MOVE && !scrolling && motion.fingers > 1) {
                scrolling = true // a second finger joined: scroll from here on
                scroll.start(0f, 0f)
            }
            val dx = motion.delta.x / density
            val dy = motion.delta.y / density
            (if (scrolling) scroll.scroll(dx, dy) else pointer.move(dx, dy, motion.dtMs))?.let(emit)
            null
        }
        if (began == Stroke.DRAG) emit(PcInput.Release(MouseButton.LEFT))
    }
}

private fun tapButton(fingers: Int): MouseButton = when {
    fingers >= MIDDLE_CLICK_FINGERS -> MouseButton.MIDDLE
    fingers == 2 -> MouseButton.RIGHT
    else -> MouseButton.LEFT
}

/** A tap with this many fingers is the middle button. */
private const val MIDDLE_CLICK_FINGERS = 3

/** Pointer events, every change consumed, until [step] returns non-null. */
private suspend fun <T : Any> AwaitPointerEventScope.awaitMotion(step: (Motion) -> T?): T {
    while (true) {
        val event = awaitPointerEvent()
        val motion = event.motion()
        event.changes.forEach { it.consume() }
        step(motion)?.let { return it }
    }
}

/** The fingers still down, and the average movement of those that were down before this event too. */
private fun PointerEvent.motion(): Motion {
    val moving = changes.filter { it.pressed && it.previousPressed }
    val delta = if (moving.isEmpty()) {
        Offset.Zero
    } else {
        moving.fold(Offset.Zero) { sum, change -> sum + (change.position - change.previousPosition) } / moving.size.toFloat()
    }
    val dt = moving.maxOfOrNull { it.uptimeMillis - it.previousUptimeMillis } ?: 0L
    return Motion(changes.count { it.pressed }, delta, dt)
}
