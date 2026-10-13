package dev.vlcremote.app.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.domain.model.AppSettings
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

class SettingsViewModel(private val repository: SettingsRepository) : ViewModel() {

    val settings: StateFlow<AppSettings> = repository.settings

    fun update(block: (AppSettings) -> AppSettings) {
        viewModelScope.launch { repository.update(block) }
    }
}
