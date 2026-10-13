package dev.vlcremote.app.data.settings

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import dev.vlcremote.app.domain.model.AppSettings
import dev.vlcremote.app.domain.model.ThemeMode
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

private val Context.settingsStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsRepository(context: Context, private val scope: CoroutineScope) {
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
        val bluetoothFallback = booleanPreferencesKey("bluetooth_fallback")
        val permissionsScreenVersion = intPreferencesKey("permissions_screen_version")
        val permissionsRequested = stringSetPreferencesKey("permissions_requested")
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
            bluetoothFallback = p[Keys.bluetoothFallback] ?: defaults.bluetoothFallback,
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
            p[Keys.bluetoothFallback] = next.bluetoothFallback
        }
    }

    // ------------------------------------------------------------ first-run permissions screen
    // Kept outside AppSettings: the hot `settings` copy starts from defaults, and these values decide
    // what the very first frame shows, so they are read from the store itself.

    /** `PermissionRegistry.screenVersion` the user has already passed; 0 = the screen was never shown. */
    suspend fun permissionsScreenVersion(): Int = store.data.first()[Keys.permissionsScreenVersion] ?: 0

    suspend fun markPermissionsScreenSeen(version: Int) {
        store.edit { p -> if ((p[Keys.permissionsScreenVersion] ?: 0) < version) p[Keys.permissionsScreenVersion] = version }
    }

    /** Debug/test hook: show the permissions screen again on the next start. */
    suspend fun resetPermissionsScreen() {
        store.edit { p -> p.remove(Keys.permissionsScreenVersion) }
    }

    /**
     * Runtime permissions the app has asked for at least once: tells "never asked" from "refused for good"
     * (both have `shouldShowRequestPermissionRationale == false`).
     */
    val requestedPermissions: StateFlow<Set<String>> =
        store.data.map { it[Keys.permissionsRequested].orEmpty() }.stateIn(scope, SharingStarted.Eagerly, emptySet())

    fun noteRequested(permissions: Collection<String>) {
        if (permissions.isEmpty()) return
        scope.launch { store.edit { p -> p[Keys.permissionsRequested] = p[Keys.permissionsRequested].orEmpty() + permissions } }
    }
}
