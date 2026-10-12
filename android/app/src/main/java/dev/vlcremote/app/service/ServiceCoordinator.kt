package dev.vlcremote.app.service

import android.content.Context
import android.util.Log
import dev.vlcremote.app.data.player.AppVisibility
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.launch

/**
 * Decides when [PlaybackService] runs:
 *  - start when connected and (VLC is playing, or the app is visible) and the
 *    "keep connection in background" setting is on;
 *  - keep running while paused/stopped for up to [IDLE_STOP_MS] (10 min), then stop;
 *  - stop immediately when disconnected or the setting is turned off.
 * The service is only ever started while the app is visible or already running, which keeps us
 * inside the foreground-service start restrictions.
 */
class ServiceCoordinator(
    private val context: Context,
    private val controller: PlayerController,
    private val settings: SettingsRepository,
    private val appVisibility: AppVisibility,
    private val scope: CoroutineScope,
) {
    private var idleStopJob: Job? = null
    private var started = false

    fun start() {
        if (started) return
        started = true
        combine(
            controller.connection,
            controller.state,
            appVisibility.visible,
            settings.settings,
        ) { connection, state, visible, prefs ->
            Decision(
                online = connection.isOnline,
                playing = state.isPlaying,
                visible = visible,
                allowed = prefs.keepConnectionInBackground,
            )
        }
            .distinctUntilChanged()
            .onEach(::apply)
            .launchIn(scope)
    }

    private data class Decision(val online: Boolean, val playing: Boolean, val visible: Boolean, val allowed: Boolean)

    private fun apply(d: Decision) {
        val running = controller.serviceRunning.value
        when {
            !d.allowed || !d.online -> {
                idleStopJob?.cancel()
                if (running) stop()
            }
            d.playing -> {
                idleStopJob?.cancel()
                if (!running && d.visible) startService()
            }
            else -> {
                // Paused/stopped: start only if visible (so it can live on after leaving), then idle-timeout.
                if (!running && d.visible) startService()
                if (idleStopJob?.isActive != true) {
                    idleStopJob = scope.launch {
                        delay(IDLE_STOP_MS)
                        if (!controller.state.value.isPlaying) stop()
                    }
                }
            }
        }
    }

    private fun startService() {
        runCatching { PlaybackService.start(context) }
            .onFailure { Log.w(TAG, "cannot start foreground service: ${it.message}") }
    }

    private fun stop() {
        PlaybackService.stop(context)
    }

    private companion object {
        const val TAG = "ServiceCoordinator"
        const val IDLE_STOP_MS = 10 * 60 * 1000L
    }
}
