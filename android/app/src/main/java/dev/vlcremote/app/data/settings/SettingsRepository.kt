package dev.vlcremote.app.data.settings

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import dev.vlcremote.app.domain.model.AppSettings
import dev.vlcremote.app.domain.model.ThemeMode
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn

private val Context.settingsStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsRepository(context: Context, scope: CoroutineScope) {
    private val store = context.applicationContext.settingsStore

    private object Keys {
        val volumeKeys = booleanPreferencesKey("volume_keys_control_pc")
        val volumeStep = intPreferencesKey("volume_key_step")
        val seekShort = intPreferencesKey("seek_short_sec")
        val seekLong = intPreferencesKey("seek_long_sec")
        val theme = stringPreferencesKey("theme")
        val trueBlack = booleanPreferencesKey("true_black")
        val dynamicColor = booleanPreferencesKey("dynamic_color")
        val keepInBackground = booleanPreferencesKey("keep_connection_in_background")
        val haptics = booleanPreferencesKey("haptics")
        val libraryMediaOnly = booleanPreferencesKey("library_media_only")
    }

    val flow: Flow<AppSettings> = store.data.map { p ->
        val defaults = AppSettings()
        AppSettings(
            volumeKeysControlPc = p[Keys.volumeKeys] ?: defaults.volumeKeysControlPc,
            volumeKeyStep = p[Keys.volumeStep] ?: defaults.volumeKeyStep,
            seekShortSec = p[Keys.seekShort] ?: defaults.seekShortSec,
            seekLongSec = p[Keys.seekLong] ?: defaults.seekLongSec,
            theme = p[Keys.theme]?.let { runCatching { ThemeMode.valueOf(it) }.getOrNull() } ?: defaults.theme,
            trueBlack = p[Keys.trueBlack] ?: defaults.trueBlack,
            dynamicColor = p[Keys.dynamicColor] ?: defaults.dynamicColor,
            keepConnectionInBackground = p[Keys.keepInBackground] ?: defaults.keepConnectionInBackground,
            haptics = p[Keys.haptics] ?: defaults.haptics,
            libraryMediaOnly = p[Keys.libraryMediaOnly] ?: defaults.libraryMediaOnly,
        )
    }

    /** Hot copy for places that need the current value synchronously (volume keys, services). */
    val settings: StateFlow<AppSettings> = flow.stateIn(scope, SharingStarted.Eagerly, AppSettings())

    suspend fun update(block: (AppSettings) -> AppSettings) {
        store.edit { p ->
            val current = settings.value
            val next = block(current)
            p[Keys.volumeKeys] = next.volumeKeysControlPc
            p[Keys.volumeStep] = next.volumeKeyStep
            p[Keys.seekShort] = next.seekShortSec
            p[Keys.seekLong] = next.seekLongSec
            p[Keys.theme] = next.theme.name
            p[Keys.trueBlack] = next.trueBlack
            p[Keys.dynamicColor] = next.dynamicColor
            p[Keys.keepInBackground] = next.keepConnectionInBackground
            p[Keys.haptics] = next.haptics
            p[Keys.libraryMediaOnly] = next.libraryMediaOnly
        }
    }
}
