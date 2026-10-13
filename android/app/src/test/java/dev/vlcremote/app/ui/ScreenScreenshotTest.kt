package dev.vlcremote.app.ui

import android.app.Application
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onRoot
import com.github.takahirom.roborazzi.captureRoboImage
import dev.vlcremote.app.data.discovery.DiscoveredHost
import dev.vlcremote.app.domain.model.AppSettings
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.ui.connection.ConnectionActions
import dev.vlcremote.app.ui.connection.ConnectionContent
import dev.vlcremote.app.ui.connection.ConnectionUiState
import dev.vlcremote.app.ui.connection.NearbyPc
import dev.vlcremote.app.ui.history.DateGroup
import dev.vlcremote.app.ui.history.HistoryActions
import dev.vlcremote.app.ui.history.HistoryContent
import dev.vlcremote.app.ui.history.HistoryGroup
import dev.vlcremote.app.ui.history.HistoryUiState
import dev.vlcremote.app.ui.history.continueCards
import dev.vlcremote.app.ui.library.Crumb
import dev.vlcremote.app.ui.library.LibraryActions
import dev.vlcremote.app.ui.library.LibraryContent
import dev.vlcremote.app.ui.library.LibraryUiState
import dev.vlcremote.app.ui.playlist.PlaylistActions
import dev.vlcremote.app.ui.playlist.PlaylistContent
import dev.vlcremote.app.ui.playlist.PlaylistUiState
import dev.vlcremote.app.ui.remote.RemoteActions
import dev.vlcremote.app.ui.remote.RemoteContent
import dev.vlcremote.app.ui.remote.RemoteUiState
import dev.vlcremote.app.ui.settings.SettingsContent
import dev.vlcremote.app.ui.theme.VlcRemoteTheme
import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.Capability
import dev.vlcremote.protocol.model.Chapters
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.EntryType
import dev.vlcremote.protocol.model.HistoryItem
import dev.vlcremote.protocol.model.NextEpisode
import dev.vlcremote.protocol.model.Playback
import dev.vlcremote.protocol.model.PlaybackStatus
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlayingItem
import dev.vlcremote.protocol.model.PlaylistItem
import dev.vlcremote.protocol.model.Track
import dev.vlcremote.protocol.model.Tracks
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.model.VlcProcessStatus
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.ParameterizedRobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/**
 * Whole screens with fake state (their stateless content composables), in both languages at the normal and the
 * largest font scale, on a phone-sized screen. See [LayoutScreenshotTest] for how to record and verify.
 */
@RunWith(ParameterizedRobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
// A plain Application: the real one starts Koin, which the content composables do not need.
@Config(sdk = [34], qualifiers = "w360dp-h780dp-xxhdpi", application = Application::class)
class ScreenScreenshotTest(private val locale: String, private val fontScale: Float) {

    @get:Rule
    val compose = createComposeRule()

    @Before
    fun setUp() {
        RuntimeEnvironment.setQualifiers("+$locale")
        RuntimeEnvironment.setFontScale(fontScale)
    }

    @Test
    fun remotePlaying() = snapshot("remote_playing") {
        RemoteContent(playing, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteWithNextEpisode() = snapshot("remote_next_episode") {
        val stopped = playing.copy(state = playing.state.copy(playback = Playback(state = PlaybackStatus.STOPPED)))
        RemoteContent(stopped, next = NextEpisode("file:///D:/Series/Show.S01E03.mkv", "Show.S01E03.1080p.WEB-DL.mkv"),
            actions = object : RemoteActions {})
    }

    @Test
    fun remoteVlcNotRunning() = snapshot("remote_vlc_not_running") {
        val state = playing.copy(state = PlayerState(vlc = VlcInfo(running = false, level = 2, status = VlcProcessStatus.NOT_RUNNING)))
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteOffline() = snapshot("remote_offline") {
        val state = RemoteUiState(connection = ConnectionState.Failed(ConnectionError.Timeout), hostName = "WIN-BBARDOEIJL4", companionMode = true)
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun connection() = snapshot("connection") {
        ConnectionContent(connection, object : ConnectionActions {})
    }

    @Test
    fun connectionOtherWays() = snapshot("connection_other_ways") {
        ConnectionContent(
            connection.copy(
                nearby = emptyList(),
                searchTimedOut = true,
                otherOptionsExpanded = true,
                otherHosts = listOf(DiscoveredHost("192.168.1.50", "HTPC-LIVINGROOM", hasCompanion = false, vlcRunning = true, vlcVersion = "3.0.21")),
            ),
            object : ConnectionActions {},
        )
    }

    @Test
    fun playlist() = snapshot("playlist") {
        PlaylistContent(playlist, object : PlaylistActions {})
    }

    @Test
    fun library() = snapshot("library") {
        LibraryContent(library, object : LibraryActions {}, onPlayed = {}, onOpenHistory = {})
    }

    @Test
    fun history() = snapshot("history") {
        HistoryContent(history, object : HistoryActions {}, onBack = {}, onPlayed = {})
    }

    @Test
    fun settings() = snapshot("settings") {
        SettingsContent(AppSettings(), update = {}, onBack = {}, versionName = "0.4.0", versionCode = 250)
    }

    private fun snapshot(name: String, content: @Composable () -> Unit) {
        compose.setContent {
            VlcRemoteTheme(AppSettings()) { Surface { content() } }
        }
        compose.onRoot().captureRoboImage("src/test/screenshots/screen_${name}_${locale}_${fontScale}x.png")
    }

    companion object {
        private val playing = RemoteUiState(
            state = PlayerState(
                vlc = VlcInfo(running = true, version = "3.0.24", level = 2, status = VlcProcessStatus.RUNNING),
                playback = Playback(state = PlaybackStatus.PLAYING, time = 2246, length = 10705),
                item = PlayingItem(title = "The Lord of the Rings: The Fellowship of the Ring / 2001 / Warner Bros."),
                tracks = Tracks(
                    audio = listOf(Track(1, "Dub / AC3 5.1", lang = "Russian"), Track(4, "BD / DTS-HD MA 5.1", lang = "English")),
                    subtitle = listOf(Track(7, "Forced / SRT", lang = "Russian")),
                    currentAudio = 1,
                    currentSubtitle = -1,
                ),
                chapters = Chapters(index = 7, count = 40, names = List(40) { if (it == 7) "The Shadow of the Past" else "Chapter ${it + 1}" }),
            ),
            connection = ConnectionState.Connected("WIN-BBARDOEIJL4", level = 2),
            capabilities = Capabilities(setOf(Capability.CURRENT_TRACK, Capability.CHAPTER_NAMES, Capability.LAUNCH_VLC)),
            hostName = "WIN-BBARDOEIJL4",
            companionMode = true,
        )

        private val playlist = PlaylistUiState(
            items = listOf(
                PlaylistItem(3, "Show.S01E01.1080p.WEB-DL.mkv", "file:///D:/3.mkv", 2701),
                PlaylistItem(4, "Show.S01E02.1080p.WEB-DL.mkv", "file:///D:/4.mkv", 2655, current = true),
                PlaylistItem(5, "Show.S01E03.1080p.WEB-DL.mkv", "file:///D:/5.mkv", 2790),
                PlaylistItem(6, "The Lord of the Rings: The Fellowship of the Ring (Extended).mkv", "file:///D:/6.mkv", 13_680),
            ),
            currentPlId = 4,
            online = true,
        )

        private val lotr = HistoryItem(
            "file:///D:/Movies/lotr.mkv", title = "The Lord of the Rings: The Fellowship of the Ring", position = 2246, length = 10705,
        )
        private val episode = HistoryItem("file:///D:/Series/Show.S01E02.mkv", name = "Show.S01E02.1080p.WEB-DL.mkv", finished = true)

        private val library = LibraryUiState(
            uri = "file:///D:/Movies/",
            crumbs = listOf(Crumb("D:", "file:///D:/"), Crumb("Movies", "file:///D:/Movies/")),
            entries = listOf(
                Entry("Extras", "file:///D:/Movies/Extras/", EntryType.DIR),
                Entry("lotr.mkv", "file:///D:/Movies/lotr.mkv", EntryType.FILE, size = 25_000_000_000, progress = 0.21),
                Entry("lotr.srt", "file:///D:/Movies/lotr.srt", EntryType.FILE, size = 120_000),
                Entry("The.Matrix.1999.2160p.UHD.BluRay.x265.mkv", "file:///D:/Movies/matrix.mkv", EntryType.FILE, finished = true),
            ),
            favorites = listOf("file:///D:/Movies/"),
            online = true,
            historyEnabled = true,
            continueCards = continueCards(listOf(lotr)),
        )

        private val history = HistoryUiState(
            available = true,
            online = true,
            items = listOf(lotr, episode),
            groups = listOf(HistoryGroup(DateGroup.TODAY, listOf(lotr)), HistoryGroup(DateGroup.THIS_WEEK, listOf(episode))),
            total = 2,
        )

        private val savedPc = SavedServer(
            id = "pc", name = "WIN-BBARDOEIJL4", host = "192.168.1.17", mode = ServerMode.COMPANION, pcId = "guid-1",
        )
        private val connection = ConnectionUiState(
            nearby = listOf(
                NearbyPc(
                    DiscoveredHost("192.168.1.17", "WIN-BBARDOEIJL4", hasCompanion = true, vlcStatus = VlcProcessStatus.RUNNING),
                    saved = savedPc,
                    paired = true,
                ),
            ),
            scanning = true,
            saved = listOf(savedPc.copy(id = "old", name = "Office PC", host = "192.168.1.30", pcId = "guid-2")),
            activeServerId = savedPc.id,
            activeServer = savedPc,
            connection = ConnectionState.Connected("WIN-BBARDOEIJL4", level = 2),
        )

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
