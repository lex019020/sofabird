package app.sofabird.service

import android.content.Context
import android.util.Log
import app.sofabird.data.player.AppVisibility
import app.sofabird.data.player.PlayerController
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.backend.somethingOnTv
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
 * Decides when [PlaybackService] (and with it the media notification) runs:
 *  - only while there is something on the TV to control ([somethingOnTv]: VLC playing or paused, YouTube open, the
 *    resume question or the next episode) — an app just opened, with nothing on the TV, shows no notification;
 *  - start then while the app is visible, when connected and the "keep connection in background" setting is on;
 *  - keep running while paused for up to [IDLE_STOP_MS] (10 min), then stop; with nothing on the TV any more stop after
 *    [NOTHING_STOP_MS] (going from one file to the next passes through "stopped" for a moment, and a service stopped
 *    in the background could not be started again);
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
    private var nothingStopJob: Job? = null
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
                onTv = state.somethingOnTv,
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
        val onTv: Boolean,
        val visible: Boolean,
        val allowed: Boolean,
    )

    private fun apply(d: Decision) {
        val running = controller.serviceRunning.value
        if (!d.reconnecting) {
            reconnectStopJob?.cancel()
            reconnectStopJob = null
        }
        if (d.onTv) {
            nothingStopJob?.cancel()
            nothingStopJob = null
        }
        when {
            d.allowed && d.reconnecting -> keepWhileReconnecting(running)
            !d.allowed || !d.online -> {
                idleStopJob?.cancel()
                if (running) stop()
            }
            d.playing -> {
                idleStopJob?.cancel()
                if (!running && d.visible) startService()
            }
            d.onTv -> {
                // Paused (or a question from the PC): start only if visible (so it can live on after leaving), then
                // idle-timeout.
                if (!running && d.visible) startService()
                stopWhenIdle()
            }
            else -> stopWhenNothingOnTv(running)
        }
    }

    /** Keep the service (and with it the process and the reconnect loop) for a while. */
    private fun keepWhileReconnecting(running: Boolean) {
        if (running && reconnectStopJob?.isActive != true) {
            reconnectStopJob = scope.launch {
                delay(RECONNECT_GRACE_MS)
                if (!controller.connection.value.isOnline) stop()
            }
        }
    }

    private fun stopWhenIdle() {
        if (idleStopJob?.isActive == true) return
        idleStopJob = scope.launch {
            delay(IDLE_STOP_MS)
            if (!controller.onTv.value.isPlaying) stop()
        }
    }

    /** Nothing on the TV: no notification "Nothing is playing" — none at all. */
    private fun stopWhenNothingOnTv(running: Boolean) {
        idleStopJob?.cancel()
        if (!running || nothingStopJob?.isActive == true) return
        nothingStopJob = scope.launch {
            delay(NOTHING_STOP_MS)
            if (!controller.onTv.value.somethingOnTv) stop()
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
        const val NOTHING_STOP_MS = 15 * 1000L
        const val RECONNECT_GRACE_MS = 2 * 60 * 1000L
    }
}
