package app.sofabird.ui

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.Audiotrack
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Subtitles
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material.icons.outlined.PlayCircle
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.domain.model.AppSettings
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.history.ContinueCard
import app.sofabird.ui.history.ContinueWatchingRow
import app.sofabird.ui.history.NextEpisodeCard
import app.sofabird.ui.remote.MoonlightSection
import app.sofabird.ui.remote.QuickAction
import app.sofabird.ui.remote.QuickActionsRow
import app.sofabird.ui.theme.SofabirdTheme
import com.github.takahirom.roborazzi.captureRoboImage
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.ParameterizedRobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/**
 * Layout screenshots of the shared controls in both languages at the normal and the largest font scale:
 * labels stay on one line (no breaks inside words), chips and buttons wrap as a whole.
 *
 * `gradlew :app:recordRoborazziDebug` updates the images in `src/test/screenshots`,
 * `gradlew :app:verifyRoborazziDebug` compares against them. A plain `test` run only renders.
 */
@RunWith(ParameterizedRobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
// A plain Application: the real one starts Koin, which these components do not need.
@Config(sdk = [34], qualifiers = "w360dp-h780dp-xxhdpi", application = Application::class)
class LayoutScreenshotTest(private val locale: String, private val fontScale: Float) {

    @get:Rule
    val compose = createComposeRule()

    @Before
    fun setUp() {
        RuntimeEnvironment.setQualifiers("+$locale")
        RuntimeEnvironment.setFontScale(fontScale)
    }

    @OptIn(ExperimentalLayoutApi::class)
    @Test
    fun controls() = snapshot("controls") {
        Column(Modifier.padding(16.dp)) {
            QuickActionsRow(
                listOf(
                    QuickAction(Icons.Default.Audiotrack, stringResource(R.string.quick_audio), "Russian", true) {},
                    QuickAction(Icons.Default.Subtitles, stringResource(R.string.quick_subtitles), stringResource(R.string.track_off), true) {},
                    QuickAction(Icons.Default.Tv, stringResource(R.string.section_display), "SAMSUNG", true) {},
                    QuickAction(Icons.Default.MoreHoriz, stringResource(R.string.chip_more), null, true) {},
                ),
            )
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(R.string.action_launch_vlc, R.string.action_quit_vlc, R.string.action_display_off, R.string.action_sleep_pc).forEach {
                    OutlinedButton(onClick = {}) { LabelText(stringResource(it)) }
                }
            }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf(R.string.theme_system, R.string.theme_dark, R.string.theme_light).forEach {
                    FilterChip(selected = it == R.string.theme_dark, onClick = {}, label = { LabelText(stringResource(it)) })
                }
                listOf(5, 10, 15, 30, 300).forEach {
                    FilterChip(selected = it == 10, onClick = {}, label = { LabelText(stringResource(R.string.seconds_short, it)) })
                }
            }
            NavigationBar {
                listOf(
                    Icons.Outlined.PlayCircle to R.string.tab_remote,
                    Icons.Default.Folder to R.string.tab_library,
                    Icons.AutoMirrored.Filled.QueueMusic to R.string.tab_playlist,
                ).forEachIndexed { i, (icon, label) ->
                    NavigationBarItem(selected = i == 0, onClick = {}, icon = { Icon(icon, null) }, label = { LabelText(stringResource(label)) })
                }
            }
        }
    }

    @Test
    fun continueWatching() = snapshot("continue_watching") {
        Column {
            ContinueWatchingRow(
                cards = listOf(
                    ContinueCard(
                        HistoryItem("file:///D:/Movies/lotr.mkv", title = "The Lord of the Rings: The Fellowship of the Ring", position = 2246, length = 10705),
                        fraction = 0.21f,
                        isNext = false,
                    ),
                    ContinueCard(HistoryItem("file:///D:/Series/Show.S01E02.1080p.WEB-DL.mkv", name = "Show.S01E02.1080p.WEB-DL.mkv"), fraction = 0f, isNext = true),
                ),
                onOpen = {},
                onMenu = {},
                onHistory = {},
            )
            NextEpisodeCard(
                NextEpisode("file:///D:/Series/Show.S01E03.1080p.WEB-DL.mkv", "Show.S01E03.1080p.WEB-DL.mkv"),
                haptics = false,
                onPlay = {},
                onDismiss = {},
                modifier = Modifier.fillMaxWidth().padding(16.dp),
            )
        }
    }

    @Test
    fun moonlight() = snapshot("moonlight") {
        Column(Modifier.padding(16.dp)) {
            MoonlightSection(host = "GAMING-PC", streaming = false, enabled = true, onStart = {}, onStop = {})
            MoonlightSection(host = "GAMING-PC", streaming = true, enabled = true, onStart = {}, onStop = {})
            MoonlightSection(host = null, streaming = false, enabled = true, onStart = {}, onStop = {})
        }
    }

    private fun snapshot(name: String, content: @Composable () -> Unit) {
        compose.setContent {
            SofabirdTheme(AppSettings()) { Surface { content() } }
        }
        compose.onRoot().captureRoboImage("src/test/screenshots/${name}_${locale}_${fontScale}x.png")
    }

    companion object {
        @JvmStatic
        @ParameterizedRobolectricTestRunner.Parameters(name = "{0}-{1}x")
        fun parameters(): List<Array<Any>> = listOf(
            arrayOf("en", 1f),
            arrayOf("ru", 1f),
            arrayOf("en", 2f),
            arrayOf("ru", 2f),
        )
    }
}
