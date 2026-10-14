package app.sofabird.data.history

import app.sofabird.protocol.model.NextEpisode
import app.sofabird.protocol.model.ResumeChoice
import app.sofabird.protocol.model.ResumePrompt
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ResumeDialogStateTest {

    private val movie = ResumePrompt("file:///D:/m.mkv", "Movie", position = 5025, length = 10705, pending = true)
    private val other = ResumePrompt("file:///D:/o.mkv", "Other", position = 400, length = 3000, pending = true)

    @Test
    fun `pending prompt is shown, a missing one hides the dialog`() {
        val shown = ResumeDialogState().onPrompt(movie)
        assertEquals(movie, shown.visible)
        assertNull(shown.onPrompt(null).visible)
    }

    @Test
    fun `not pending or without uri is never shown`() {
        assertNull(ResumeDialogState().onPrompt(movie.copy(pending = false)).visible)
        assertNull(ResumeDialogState().onPrompt(movie.copy(uri = "")).visible)
    }

    @Test
    fun `first tap wins, a second tap sends nothing`() {
        val shown = ResumeDialogState().onPrompt(movie)
        val (afterFirst, sendFirst) = shown.onAnswer(ResumeChoice.RESUME)
        assertTrue(sendFirst)
        assertNull(afterFirst.visible)
        assertEquals(ResumeChoice.RESUME, afterFirst.answered)
        val (afterSecond, sendSecond) = afterFirst.onAnswer(ResumeChoice.RESTART)
        assertFalse(sendSecond)
        assertEquals(ResumeChoice.RESUME, afterSecond.answered)
    }

    @Test
    fun `answer without a prompt is not sent`() {
        val (state, send) = ResumeDialogState().onAnswer(ResumeChoice.RESTART)
        assertFalse(send)
        assertNull(state.answered)
    }

    @Test
    fun `repeated state pushes keep the dialog hidden after our answer`() {
        val answered = ResumeDialogState().onPrompt(movie).onAnswer(ResumeChoice.RESUME).first
        // The PC has not applied it yet and repeats the same prompt (fresh fields).
        val repeated = answered.onPrompt(movie.copy(position = 5026))
        assertNull(repeated.visible)
        assertEquals(5026L, repeated.pending!!.position)
    }

    @Test
    fun `another phone answered first - the prompt disappears`() {
        val shown = ResumeDialogState().onPrompt(movie)
        assertNull(shown.onPrompt(null).visible)
    }

    @Test
    fun `failed answer shows the prompt again while the PC still waits`() {
        val answered = ResumeDialogState().onPrompt(movie).onAnswer(ResumeChoice.RESUME).first
        assertEquals(movie, answered.onAnswerFailed(movie.uri).visible)
        // ...but not when the PC already dropped it (someone else answered).
        assertNull(answered.onPrompt(null).onAnswerFailed(movie.uri).visible)
        // ...and not for a different file prompted meanwhile that this phone did not answer: it stays visible.
        val switched = answered.onPrompt(other)
        assertEquals(other, switched.onAnswerFailed(movie.uri).visible)
    }

    @Test
    fun `a new file after the answer is asked again`() {
        val answered = ResumeDialogState().onPrompt(movie).onAnswer(ResumeChoice.RESTART).first
        assertEquals(other, answered.onPrompt(other).visible)
        // The same file opened again later: the PC cleared the prompt in between.
        assertEquals(movie, answered.onPrompt(null).onPrompt(movie).visible)
    }

    @Test
    fun `next offer hides after play or dismiss until the PC changes it`() {
        val ep2 = NextEpisode("file:///s/e2.mkv", "E2")
        val ep3 = NextEpisode("file:///s/e3.mkv", "E3")
        val offered = NextOfferState().onOffer(ep2)
        assertEquals(ep2, offered.visible)
        val handled = offered.onHandled()
        assertNull(handled.visible)
        assertNull(handled.onOffer(ep2).visible)
        assertEquals(ep3, handled.onOffer(ep3).visible)
        // Dropped by the PC, offered again later: shown.
        assertEquals(ep2, handled.onOffer(null).onOffer(ep2).visible)
        assertNull(NextOfferState().onOffer(NextEpisode("")).visible)
    }
}
