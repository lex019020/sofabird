package app.sofabird.ui.remote

import app.sofabird.data.player.PlayerController
import app.sofabird.domain.model.MoonlightChoice
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.companion.CompanionErrorException
import app.sofabird.protocol.companion.ErrorCode
import app.sofabird.protocol.model.MoonlightHost
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * What the Moonlight sheet shows ([MoonlightSheet]): the computers paired in Moonlight on the PC with their apps, and
 * which computer and app are chosen — picked in the sheet, else what this phone last started on this PC ([last]),
 * else the first computer and its Desktop.
 */
data class MoonlightUi(
    val hosts: List<MoonlightHost> = emptyList(),
    /** The list arrived since the sheet opened. */
    val loaded: Boolean = false,
    /** `moonlightHosts` is running: [refreshing] when the computers are asked for their apps (up to ~15 s). */
    val loading: Boolean = false,
    val refreshing: Boolean = false,
    /** The last request failed (shown in the sheet). */
    val error: Throwable? = null,
    /** Picked in the sheet; `null` — the remembered one. */
    val pickedHost: String? = null,
    val pickedApp: String? = null,
    /** What this phone last started through Moonlight on this PC (kept with the saved PC). */
    val last: MoonlightChoice? = null,
) {
    /** The chosen computer; `null` — none paired (or the list has not arrived). */
    val host: MoonlightHost?
        get() = hosts.named(pickedHost) ?: hosts.named(last?.host) ?: hosts.firstOrNull()

    /** The chosen app of [host]: picked, else the last one started there, else Desktop, else its first. */
    val app: String?
        get() {
            val host = host ?: return null
            val choices = host.choices
            val remembered = last?.takeIf { it.host.equals(host.name, ignoreCase = true) }?.app
            return choices.named(pickedApp) ?: choices.named(remembered) ?: choices.named(MoonlightHost.DESKTOP) ?: choices.first()
        }

    /**
     * After `moonlightHosts`: its list, or the failure to show. A companion from before the list (`unsupported`) still
     * starts its first computer's Desktop: [stateHost] (`state.system.moonlightHost`) is offered alone then.
     */
    fun loaded(result: Result<List<MoonlightHost>>, stateHost: String?): MoonlightUi {
        val error = result.exceptionOrNull()
        val hosts = when {
            error == null -> result.getOrThrow()
            (error as? CompanionErrorException)?.code == ErrorCode.UNSUPPORTED && stateHost != null -> listOf(MoonlightHost(stateHost))
            else -> return copy(error = error, loading = false, refreshing = false)
        }
        return copy(hosts = hosts, loaded = true, loading = false, refreshing = false, error = null)
    }

    private fun List<MoonlightHost>.named(name: String?): MoonlightHost? =
        name?.let { n -> firstOrNull { it.name.equals(n, ignoreCase = true) } }

    private fun List<String>.named(name: String?): String? = name?.let { n -> firstOrNull { it.equals(n, ignoreCase = true) } }
}

/**
 * The Moonlight sheet's list and choice, for [RemoteViewModel]: loads `moonlightHosts` when the sheet opens and on
 * "Refresh", keeps what is picked, and starts the stream (remembering the choice for next time).
 */
class MoonlightChooser(private val controller: PlayerController, private val scope: CoroutineScope) {
    private val _state = MutableStateFlow(MoonlightUi())
    val state: StateFlow<MoonlightUi> = _state.asStateFlow()
    private var job: Job? = null

    /** The sheet opened: the remembered choice again, and the list as the PC knows it. */
    fun open() {
        _state.update { it.copy(pickedHost = null, pickedApp = null, error = null) }
        load(refresh = false)
    }

    /** "Refresh": every computer is asked for its apps (a refresh already running is not started again). */
    fun refresh() = load(refresh = true)

    private fun load(refresh: Boolean) {
        if (!controller.capabilities.value.moonlight) return
        if (job?.isActive == true && (!refresh || _state.value.refreshing)) return
        job?.cancel()
        job = scope.launch {
            _state.update { it.copy(loading = true, refreshing = refresh, error = null) }
            // A PC that does not answer in time is a failure to show, not a cancellation of this load.
            val result = try {
                controller.moonlightHosts(refresh)
            } catch (e: TimeoutCancellationException) {
                Result.failure(e)
            }
            _state.update { it.loaded(result, controller.state.value.system?.moonlightHost) }
        }
    }

    /** A computer (its remembered app, or Desktop) or one of its apps picked in the sheet. */
    fun pick(host: String, app: String? = null) {
        _state.update { it.copy(pickedHost = host, pickedApp = app) }
    }

    /** Shows [app] of [host] on the TV (VLC pauses) and remembers the choice. */
    fun start(host: String, app: String) {
        controller.rememberMoonlight(MoonlightChoice(host, app))
        controller.sendSystem(SystemCommand.MoonlightStart(host = host, app = app))
    }
}
