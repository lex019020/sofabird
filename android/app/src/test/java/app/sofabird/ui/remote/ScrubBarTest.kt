package app.sofabird.ui.remote

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = Application::class)
class ScrubBarTest {

    @get:Rule
    val compose = createComposeRule()

    private fun ui(state: PlaybackStatus, time: Long) = RemoteUiState(
        state = PlayerState(playback = Playback(state = state, time = time, length = 10705)),
        connection = ConnectionState.Connected("PC"),
    )

    @Test
    fun `a pause after the first state shows its own position`() {
        // The bar was first shown before the connection (position 0); then VLC reports a pause at 50:34.
        var ui by mutableStateOf(ui(PlaybackStatus.STOPPED, 0))
        compose.setContent { ScrubBar(ui, object : RemoteActions {}) }
        ui = ui(PlaybackStatus.PAUSED, 3034)
        compose.onNodeWithText("50:34").assertIsDisplayed()
    }
}
