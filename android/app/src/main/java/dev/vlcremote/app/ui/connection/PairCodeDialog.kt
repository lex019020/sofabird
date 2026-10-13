package dev.vlcremote.app.ui.connection

import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.DialogProperties
import dev.vlcremote.app.R
import dev.vlcremote.app.data.player.PairingFlow
import dev.vlcremote.app.data.player.PairingState
import kotlinx.coroutines.delay

/**
 * "Enter the code from the computer screen": six big digit cells, numeric keyboard opens by itself, the code
 * is sent as soon as the sixth digit is typed. A rejected code clears the field and keeps the dialog.
 */
@Composable
fun PairCodeDialog(
    state: PairingState.EnterCode,
    onSubmit: (String) -> Unit,
    onCancel: () -> Unit,
) {
    var code by remember { mutableStateOf("") }
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current

    // New attempt after a rejected code: start over.
    LaunchedEffect(state.attempt) {
        if (state.attempt > 0) {
            code = ""
            runCatching { focus.requestFocus() }
        }
    }
    LaunchedEffect(Unit) {
        delay(150) // the dialog window must be attached before it can take focus
        runCatching { focus.requestFocus() }
        keyboard?.show()
    }

    fun update(value: String) {
        if (state.submitting) return
        val digits = value.filter(Char::isDigit).take(PairingFlow.CODE_LENGTH)
        code = digits
        if (digits.length == PairingFlow.CODE_LENGTH) onSubmit(digits)
    }

    AlertDialog(
        onDismissRequest = onCancel,
        properties = DialogProperties(dismissOnClickOutside = false),
        title = { Text(stringResource(R.string.pair_dialog_title)) },
        text = {
            Column {
                Text(stringResource(R.string.pair_dialog_hint, state.target.name), style = MaterialTheme.typography.bodyMedium)
                Spacer(Modifier.height(20.dp))
                BasicTextField(
                    value = code,
                    onValueChange = ::update,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { if (code.length == PairingFlow.CODE_LENGTH) onSubmit(code) }),
                    modifier = Modifier
                        .fillMaxWidth()
                        .focusRequester(focus),
                    decorationBox = { _ -> CodeCells(code, hasError = state.error != null) },
                )
                Spacer(Modifier.height(16.dp))
                when {
                    state.submitting -> Row(verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(8.dp))
                        Text(stringResource(R.string.pair_dialog_checking), style = MaterialTheme.typography.bodyMedium)
                    }
                    state.error != null -> Text(
                        pairCodeErrorText(state.error),
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                Spacer(Modifier.height(8.dp))
                Text(
                    stringResource(R.string.pair_dialog_fallback),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onSubmit(code) },
                enabled = code.length == PairingFlow.CODE_LENGTH && !state.submitting,
            ) { Text(stringResource(R.string.action_connect)) }
        },
        dismissButton = {
            TextButton(onClick = onCancel) { Text(stringResource(R.string.cancel)) }
        },
    )
}

@Composable
private fun CodeCells(code: String, hasError: Boolean) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        repeat(PairingFlow.CODE_LENGTH) { i ->
            val current = i == code.length
            val color = when {
                hasError && code.isEmpty() -> MaterialTheme.colorScheme.error
                current -> MaterialTheme.colorScheme.primary
                else -> MaterialTheme.colorScheme.outline
            }
            Box(
                Modifier
                    .weight(1f)
                    .aspectRatio(0.8f)
                    .border(if (current) 2.dp else 1.dp, color, RoundedCornerShape(10.dp)),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    code.getOrNull(i)?.toString() ?: "",
                    fontSize = 30.sp,
                    fontWeight = FontWeight.SemiBold,
                    fontFamily = FontFamily.Monospace,
                )
            }
        }
    }
}
