package app.sofabird.ui.touchpad

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.sofabird.R
import app.sofabird.protocol.backend.PcInput
import app.sofabird.ui.common.LabelText
import org.koin.androidx.compose.koinViewModel

/** "Touchpad and keyboard" from "More": the whole screen is the PC's touchpad, with the keyboard under it. */
@Composable
fun TouchpadScreen(onBack: () -> Unit, viewModel: TouchpadViewModel = koinViewModel()) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    TouchpadContent(ui, viewModel::input, onBack)
}

/** [keyboardOpen]: with the keyboard shown from the start (screenshots). */
@Composable
fun TouchpadContent(ui: TouchpadUiState, onInput: (PcInput) -> Unit, onBack: () -> Unit, keyboardOpen: Boolean = false) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { LabelText(stringResource(R.string.touchpad_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back)) }
                },
            )
        },
    ) { padding ->
        val body = Modifier
            .fillMaxSize()
            .padding(padding)
            .consumeWindowInsets(padding)
            .imePadding() // the phone's keyboard pushes the keys and the field up; the pad gives way
            .padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 16.dp)
        when {
            !ui.online -> Message(stringResource(R.string.err_not_connected), body)
            !ui.available -> Message(stringResource(R.string.touchpad_unavailable), body)
            else -> Touchpad(onInput, ui.haptics, body, keyboardOpen = keyboardOpen)
        }
    }
}

@Composable
private fun Message(text: String, modifier: Modifier) {
    Box(modifier, contentAlignment = Alignment.Center) {
        Text(
            text,
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}
