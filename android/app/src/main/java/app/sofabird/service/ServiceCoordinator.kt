package app.sofabird.service

import android.content.Context
import android.util.Log
import app.sofabird.data.player.AppVisibility
import app.sofabird.data.player.PlayerController
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.model.ConnectionState
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
 *  - start when connected and (the TV plays — VLC, or YouTube in the TV browser — or the app is visible) and the
 *    "keep connection in background" setting is on;
 *  - keep running while paused/stopped for up to [IDLE_STOP_MS] (10 min), then stop;
 *  - stop immediately when disconnected or the setting is turned off;
 *  - while the link is re-established (Wi-Fi dropped → Bluetooth, PC rebooting) keep running for up
 *    to [RECONNECT_GRACE_MS]: once stopped in the background it could not be started again, and the
 *    reconnect — e.g. the switch to RFCOMM — would be lost with it.
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
    private var reconnectStopJob: Job? = null
    private var started = false

    fun start() {
        if (started) return
        started = true
        combine(
            controller.connection,
            controller.onTv,
            appVisibility.visible,
            settings.settings,
        ) { connection, state, visible, prefs ->
            Decision(
                online = connection.isOnline,
                reconnecting = connection is ConnectionState.Connecting,
                playing = state.isPlaying,
                visible = visible,
                allowed = prefs.keepConnectionInBackground,
            )
        }
            .distinctUntilChanged()
            .onEach(::apply)
            .launchIn(scope)
    }

    private data class Decision(
        val online: Boolean,
        val reconnecting: Boolean,
        val playing: Boolean,
        val visible: Boolean,
        val allowed: Boolean,
    )

    private fun apply(d: Decision) {
        val running = controller.serviceRunning.value
        if (!d.reconnecting) {
            reconnectStopJob?.cancel()
            reconnectStopJob = null
        }
        when {
            d.allowed && d.reconnecting -> {
                // Keep the service (and with it the process and the reconnect loop) for a while.
                if (running && reconnectStopJob?.isActive != true) {
                    reconnectStopJob = scope.launch {
                        delay(RECONNECT_GRACE_MS)
                        if (!controller.connection.value.isOnline) stop()
                    }
                }
            }
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
        const val RECONNECT_GRACE_MS = 2 * 60 * 1000L
    }
}
