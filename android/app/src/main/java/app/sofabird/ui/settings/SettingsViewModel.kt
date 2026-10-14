package app.sofabird.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.domain.model.AppSettings
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

class SettingsViewModel(private val repository: SettingsRepository) : ViewModel() {

    val settings: StateFlow<AppSettings> = repository.settings

    fun update(block: (AppSettings) -> AppSettings) {
        viewModelScope.launch { repository.update(block) }
    }
}
