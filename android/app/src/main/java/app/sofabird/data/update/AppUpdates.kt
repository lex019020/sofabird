package app.sofabird.data.update

import android.content.Context
import android.content.pm.PackageInstaller
import android.util.Log
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import app.sofabird.BuildConfig
import app.sofabird.data.player.AppVisibility
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

private val Context.updatesStore: DataStore<Preferences> by preferencesDataStore(name = "updates")

/** Where the app's own update is. */
sealed interface UpdateState {
    /** Not checked yet in this run. */
    data object Idle : UpdateState
    data object Checking : UpdateState
    data object UpToDate : UpdateState
    data object CheckFailed : UpdateState
    data class Available(val update: AvailableUpdate) : UpdateState

    /** Android has not allowed Sofabird to install apps yet: its settings page is open. */
    data class NeedsPermission(val update: AvailableUpdate) : UpdateState

    /** [progress] 0..1, `null` — the size is unknown. */
    data class Downloading(val update: AvailableUpdate, val progress: Float?) : UpdateState

    /** Handed to the system installer, which asks the user to confirm. */
    data class Installing(val update: AvailableUpdate) : UpdateState
    data class InstallFailed(val update: AvailableUpdate) : UpdateState
}

/** The version this state is about, if any. */
val UpdateState.update: AvailableUpdate?
    get() = when (this) {
        is UpdateState.Available -> update
        is UpdateState.NeedsPermission -> update
        is UpdateState.Downloading -> update
        is UpdateState.Installing -> update
        is UpdateState.InstallFailed -> update
        UpdateState.Idle, UpdateState.Checking, UpdateState.UpToDate, UpdateState.CheckFailed -> null
    }

/**
 * The app updates itself from the project's GitHub releases, like the companion does: it looks for a newer build of its
 * channel when it comes to the front (at most every [CHECK_EVERY_MS]), asks once per version with a dialog when the
 * new builds change something users notice, downloads the APK and hands it to the system installer (Android asks the
 * user to confirm; the signature must match, so only our builds install). Developer builds (debug, `-local`) never
 * update themselves.
 */
class AppUpdates(
    context: Context,
    private val visibility: AppVisibility,
    private val scope: CoroutineScope,
    installedVersion: String = BuildConfig.VERSION_NAME,
    debugBuild: Boolean = BuildConfig.DEBUG,
) {
    private val context = context.applicationContext
    private val store = this.context.updatesStore
    private val installed = AppVersion.parse(installedVersion)
    private val userAgent = "Sofabird/$installedVersion (Android)"
    private val http = OkHttpClient.Builder()
        .connectTimeout(CONNECT_TIMEOUT_S, TimeUnit.SECONDS)
        .readTimeout(READ_TIMEOUT_S, TimeUnit.SECONDS)
        .build()

    /** Test builds and releases update themselves; a debug or a `-local` build does not. */
    val enabled: Boolean = !debugBuild && installed != null &&
        (!installed.isPreRelease || installed.preRelease.first().startsWith(TEST_BUILD_PREFIX))

    private object Keys {
        val channel = stringPreferencesKey("channel")
        val lastCheck = longPreferencesKey("last_check")
        val skipped = stringPreferencesKey("skipped_version")
    }

    val channel: StateFlow<UpdateChannel> = store.data
        .map { p -> p[Keys.channel]?.let { runCatching { UpdateChannel.valueOf(it) }.getOrNull() } ?: UpdateChannel.of(installed) }
        .stateIn(scope, SharingStarted.Eagerly, UpdateChannel.of(installed))

    private val _state = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = _state.asStateFlow()

    private val _promptOpen = MutableStateFlow(false)

    /** The update dialog is open: a check found a noteworthy version not put off with "Later". */
    val promptOpen: StateFlow<Boolean> = _promptOpen.asStateFlow()

    private var job: Job? = null

    fun start() {
        if (!enabled) return
        scope.launch(Dispatchers.IO) { updatesDir().deleteRecursively() } // an APK of the version now installed
        scope.launch { visibility.visible.collect { if (it) checkIfDue() } }
    }

    fun checkNow() = check(manual = true)

    fun setChannel(value: UpdateChannel) {
        scope.launch {
            store.edit { it[Keys.channel] = value.name }
            check(manual = true)
        }
    }

    /** "Later" in the dialog: it does not ask about this version again (the settings still offer it). */
    fun later(update: AvailableUpdate) {
        _promptOpen.value = false
        scope.launch { store.edit { it[Keys.skipped] = update.version } }
    }

    fun install(update: AvailableUpdate) {
        if (job?.isActive == true) return
        if (!ApkInstaller.canInstall(context)) {
            _state.value = UpdateState.NeedsPermission(update)
            ApkInstaller.openPermissionSettings(context)
            return
        }
        job = scope.launch {
            _state.value = UpdateState.Downloading(update, progress = null)
            val file = runCatching { download(update) }
                .onFailure { Log.w(TAG, "Download of ${update.version} failed", it) }
                .getOrNull()
            if (file == null || !ApkInstaller.matches(context, file, update.version)) {
                _state.value = UpdateState.InstallFailed(update)
                return@launch
            }
            _state.value = UpdateState.Installing(update)
            _promptOpen.value = false
            runCatching { withContext(Dispatchers.IO) { ApkInstaller.install(context, file) } }
                .onFailure {
                    Log.w(TAG, "Install of ${update.version} failed", it)
                    _state.value = UpdateState.InstallFailed(update)
                }
        }
    }

    /** The system installer's answer (success replaces this process, so only a failure or a "Cancel" gets here). */
    fun installFinished(status: Int, message: String?) {
        val update = (_state.value as? UpdateState.Installing)?.update ?: return
        Log.i(TAG, "Installer: status $status, $message")
        _state.value = if (status == PackageInstaller.STATUS_FAILURE_ABORTED) {
            UpdateState.Available(update)
        } else {
            UpdateState.InstallFailed(update)
        }
    }

    private fun checkIfDue() {
        scope.launch {
            val last = store.data.first()[Keys.lastCheck] ?: 0L
            if (_state.value == UpdateState.Idle || System.currentTimeMillis() - last >= CHECK_EVERY_MS) check(manual = false)
        }
    }

    private fun check(manual: Boolean) {
        val version = installed
        if (!enabled || version == null || job?.isActive == true) return
        job = scope.launch {
            if (manual) _state.value = UpdateState.Checking
            val releases = runCatching { withContext(Dispatchers.IO) { fetchReleases() } }
                .onFailure { Log.w(TAG, "Update check failed", it) }
                .getOrNull()
            if (releases == null) {
                // A quiet check that failed (no internet) changes nothing; "Check now" says so.
                if (manual || _state.value == UpdateState.Checking) _state.value = UpdateState.CheckFailed
                return@launch
            }
            store.edit { it[Keys.lastCheck] = System.currentTimeMillis() }
            val update = ReleaseFeed.newest(releases, channel.value, version)
            _state.value = if (update == null) UpdateState.UpToDate else UpdateState.Available(update)
            val skipped = store.data.first()[Keys.skipped]
            if (update != null && update.noteworthy && update.version != skipped) _promptOpen.value = true
        }
    }

    private fun fetchReleases(): List<GitHubRelease> {
        val request = Request.Builder()
            .url(ReleaseFeed.URL)
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", userAgent)
            .build()
        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("GitHub: HTTP ${response.code}")
            return ReleaseFeed.parse(response.body?.string() ?: throw IOException("GitHub: no body"))
        }
    }

    private suspend fun download(update: AvailableUpdate): File = withContext(Dispatchers.IO) {
        val dir = updatesDir().apply { mkdirs() }
        val file = File(dir, "Sofabird-${update.version}.apk")
        dir.listFiles()?.filter { it != file }?.forEach { it.delete() }
        if (update.size > 0 && file.length() == update.size) return@withContext file
        val part = File(dir, file.name + ".part")
        val request = Request.Builder().url(update.apkUrl).header("User-Agent", userAgent).build()
        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("Download: HTTP ${response.code}")
            val body = response.body ?: throw IOException("Download: no body")
            val total = body.contentLength().takeIf { it > 0 } ?: update.size
            body.byteStream().use { input ->
                part.outputStream().use { output ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var done = 0L
                    var shown = -1
                    while (true) {
                        ensureActive()
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        done += read
                        val percent = if (total > 0) (done * PERCENT / total).toInt() else -1
                        if (percent != shown) {
                            shown = percent
                            _state.value = UpdateState.Downloading(update, if (total > 0) done.toFloat() / total else null)
                        }
                    }
                }
            }
        }
        if (!part.renameTo(file)) throw IOException("Download: cannot keep ${file.name}")
        file
    }

    private fun updatesDir() = File(context.cacheDir, "updates")

    companion object {
        private const val TAG = "AppUpdates"
        private const val TEST_BUILD_PREFIX = "beta"
        private const val CHECK_EVERY_MS = 6 * 60 * 60 * 1000L
        private const val CONNECT_TIMEOUT_S = 10L
        private const val READ_TIMEOUT_S = 30L
        private const val BUFFER_SIZE = 64 * 1024
        private const val PERCENT = 100
    }
}
