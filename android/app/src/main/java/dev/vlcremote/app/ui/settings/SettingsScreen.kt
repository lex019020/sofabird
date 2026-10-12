package dev.vlcremote.app.ui.settings

import android.os.Build
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.vlcremote.app.R
import dev.vlcremote.app.domain.model.AppSettings
import dev.vlcremote.app.domain.model.ThemeMode
import org.koin.androidx.compose.koinViewModel

@Composable
fun SettingsScreen(onBack: () -> Unit, viewModel: SettingsViewModel = koinViewModel()) {
    val s by viewModel.settings.collectAsStateWithLifecycle()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.settings)) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back)) } },
            )
        },
    ) { padding ->
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp),
        ) {
            Section(stringResource(R.string.settings_control))
            SwitchRow(
                title = stringResource(R.string.settings_volume_keys),
                subtitle = stringResource(R.string.settings_volume_keys_hint),
                checked = s.volumeKeysControlPc,
            ) { v -> viewModel.update { it.copy(volumeKeysControlPc = v) } }
            ChoiceRow(
                title = stringResource(R.string.settings_volume_step),
                options = AppSettings.VOLUME_STEPS,
                selected = s.volumeKeyStep,
                label = { "${it * 100 / 256} %" },
            ) { v -> viewModel.update { it.copy(volumeKeyStep = v) } }
            ChoiceRow(
                title = stringResource(R.string.settings_seek_short),
                options = AppSettings.SEEK_SHORT_STEPS,
                selected = s.seekShortSec,
                label = { stringResource(R.string.seconds_short, it) },
            ) { v -> viewModel.update { it.copy(seekShortSec = v) } }
            ChoiceRow(
                title = stringResource(R.string.settings_seek_long),
                options = AppSettings.SEEK_LONG_STEPS,
                selected = s.seekLongSec,
                label = { stringResource(R.string.seconds_short, it) },
            ) { v -> viewModel.update { it.copy(seekLongSec = v) } }
            SwitchRow(title = stringResource(R.string.settings_haptics), subtitle = null, checked = s.haptics) { v ->
                viewModel.update { it.copy(haptics = v) }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))
            Section(stringResource(R.string.settings_appearance))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(vertical = 4.dp)) {
                ThemeMode.entries.forEach { mode ->
                    FilterChip(
                        selected = s.theme == mode,
                        onClick = { viewModel.update { it.copy(theme = mode) } },
                        label = {
                            Text(
                                stringResource(
                                    when (mode) {
                                        ThemeMode.SYSTEM -> R.string.theme_system
                                        ThemeMode.DARK -> R.string.theme_dark
                                        ThemeMode.LIGHT -> R.string.theme_light
                                    },
                                ),
                            )
                        },
                    )
                }
            }
            SwitchRow(
                title = stringResource(R.string.settings_true_black),
                subtitle = stringResource(R.string.settings_true_black_hint),
                checked = s.trueBlack,
                enabled = s.theme != ThemeMode.LIGHT,
            ) { v -> viewModel.update { it.copy(trueBlack = v) } }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                SwitchRow(title = stringResource(R.string.settings_dynamic_color), subtitle = null, checked = s.dynamicColor) { v ->
                    viewModel.update { it.copy(dynamicColor = v) }
                }
            }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))
            Section(stringResource(R.string.settings_background))
            SwitchRow(
                title = stringResource(R.string.settings_keep_connection),
                subtitle = stringResource(R.string.settings_keep_connection_hint),
                checked = s.keepConnectionInBackground,
            ) { v -> viewModel.update { it.copy(keepConnectionInBackground = v) } }

            HorizontalDivider(Modifier.padding(vertical = 12.dp))
            Section(stringResource(R.string.settings_about))
            Text(stringResource(R.string.about_text), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text("", Modifier.padding(bottom = 24.dp))
        }
    }
}

@Composable
private fun Section(text: String) {
    Text(text, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
}

@Composable
private fun SwitchRow(title: String, subtitle: String?, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled) { onChange(!checked) }
            .padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

@Composable
private fun <T> ChoiceRow(title: String, options: List<T>, selected: T, label: @Composable (T) -> String, onSelect: (T) -> Unit) {
    Column(Modifier.padding(vertical = 4.dp)) {
        Text(title, style = MaterialTheme.typography.bodyLarge)
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            options.forEach { option ->
                FilterChip(selected = option == selected, onClick = { onSelect(option) }, label = { Text(label(option)) })
            }
        }
    }
}
