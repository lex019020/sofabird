package dev.vlcremote.app.data.history

import android.util.Log
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.companion.CompanionErrorException
import dev.vlcremote.protocol.model.NextEpisode
import dev.vlcremote.protocol.model.ResumeChoice
import dev.vlcremote.protocol.model.ResumePrompt
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * The resume prompt as this phone shows it. The PC reports [pending] in every `state` until someone
 * answers; every connected phone shows it, the first answer wins (the PC then drops `state.resume`
 * and the dialog disappears everywhere). Pure logic, unit-tested.
 */
data class ResumeDialogState(
    /** The prompt the PC waits on; `null` = none (or no connection / no `history` capability). */
    val pending: ResumePrompt? = null,
    /** This phone already answered [pending]: hidden while the PC applies the answer. */
    val answered: ResumeChoice? = null,
) {
    /** What the dialog / notification shows; `null` = nothing. */
    val visible: ResumePrompt? get() = pending.takeIf { answered == null }

    /** A new `state.resume` from the PC (`null` when it is gone or the link is down). */
    fun onPrompt(prompt: ResumePrompt?): ResumeDialogState {
        val p = prompt?.takeIf { it.pending && it.uri.isNotBlank() }
        return when {
            p == null -> ResumeDialogState()
            // The same prompt again (every state push repeats it): keep our answer, take fresh fields.
            pending != null && p.uri == pending.uri -> copy(pending = p)
            else -> ResumeDialogState(pending = p)
        }
    }

    /** The user picked [choice]. Returns the new state and whether to send it: only the first tap counts. */
    fun onAnswer(choice: ResumeChoice): Pair<ResumeDialogState, Boolean> =
        if (visible == null) this to false else copy(answered = choice) to true

    /** The answer was not delivered: show the prompt again if the PC still waits for it. */
    fun onAnswerFailed(uri: String): ResumeDialogState =
        if (pending?.uri == uri) copy(answered = null) else this
}

/** The next-episode offer as shown on this phone: hidden locally right after "Dismiss" / "Play". */
data class NextOfferState(
    val offer: NextEpisode? = null,
    /** Offer the user already acted on (until the PC drops or changes it). */
    val handledUri: String? = null,
) {
    val visible: NextEpisode? get() = offer?.takeIf { it.uri.isNotBlank() && it.uri != handledUri }

    fun onOffer(next: NextEpisode?): NextOfferState =
        if (next == null || next.uri.isBlank()) NextOfferState() else copy(offer = next)

    fun onHandled(): NextOfferState = copy(handledUri = offer?.uri)
}

/**
 * App-wide owner of the resume prompt and the next-episode offer (companion `history`): the dialog
 * on any screen, the card on the remote and the notification actions all go through here, so a tap
 * in one place hides the others at once.
 */
class WatchPrompts(
    private val controller: PlayerController,
    private val scope: CoroutineScope,
) {
    private val resumeState = MutableStateFlow(ResumeDialogState())
    private val nextState = MutableStateFlow(NextOfferState())

    /** The prompt to show now; `null` = none. */
    val resume: StateFlow<ResumePrompt?> = resumeState
        .map { it.visible }
        .stateIn(scope, SharingStarted.Eagerly, null)

    /** The next episode to offer now; `null` = none. */
    val next: StateFlow<NextEpisode?> = nextState
        .map { it.visible }
        .stateIn(scope, SharingStarted.Eagerly, null)

    init {
        val usable = combine(controller.capabilities, controller.connection) { caps, c -> caps.history && c.isOnline }
        combine(usable, controller.state) { ok, s -> if (ok) s.resume else null }
            .distinctUntilChanged()
            .onEach { prompt -> resumeState.update { it.onPrompt(prompt) } }
            .launchIn(scope)
        combine(usable, controller.state) { ok, s -> if (ok) s.next else null }
            .distinctUntilChanged()
            .onEach { offer -> nextState.update { it.onOffer(offer) } }
            .launchIn(scope)
    }

    /** "Continue from …" / "From the beginning" — from the dialog or the notification. */
    fun answer(choice: ResumeChoice) {
        val (updated, send) = resumeState.value.onAnswer(choice)
        if (!send) return
        val uri = updated.pending?.uri ?: return
        resumeState.value = updated
        scope.launch {
            val result = controller.sendQuiet(PlayerCommand.Resume(choice))
            result.onFailure { e ->
                Log.d(TAG, "resume answer failed: ${e.message}")
                // The PC rejected it — most likely another phone answered first and the next state
                // drops the prompt anyway; give that state a moment before showing the dialog again.
                if (e is CompanionErrorException) delay(REJECTED_GRACE_MS)
                resumeState.update { it.onAnswerFailed(uri) }
            }
        }
    }

    /** "Play" on the next-episode card / "Play next" in the notification. */
    fun playNext() {
        val offer = nextState.value.visible ?: return
        nextState.update { it.onHandled() }
        controller.send(PlayerCommand.Open(offer.uri))
    }

    /** "Dismiss" on the next-episode card. */
    fun dismissNext() {
        if (nextState.value.visible == null) return
        nextState.update { it.onHandled() }
        scope.launch { controller.sendQuiet(PlayerCommand.DismissNext) }
    }

    private companion object {
        const val TAG = "WatchPrompts"
        const val REJECTED_GRACE_MS = 700L
    }
}
