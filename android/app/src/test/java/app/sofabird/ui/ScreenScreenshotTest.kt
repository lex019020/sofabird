package app.sofabird.ui

import android.app.Application
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onRoot
import app.sofabird.data.discovery.DiscoveredHost
import app.sofabird.data.player.PcWaker
import app.sofabird.domain.model.AppSettings
import app.sofabird.domain.model.SavedServer
import app.sofabird.protocol.model.Capabilities
import app.sofabird.protocol.model.Capability
import app.sofabird.protocol.model.Chapters
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.EntryType
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.protocol.model.LibraryEpisode
import app.sofabird.protocol.model.LibraryMovie
import app.sofabird.protocol.model.LibraryShow
import app.sofabird.protocol.model.MediaFile
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlayingItem
import app.sofabird.protocol.model.PlaylistItem
import app.sofabird.protocol.model.ShowDetails
import app.sofabird.protocol.model.SleepTimerState
import app.sofabird.protocol.model.SystemState
import app.sofabird.protocol.model.Track
import app.sofabird.protocol.model.Tracks
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.model.VlcProcessStatus
import app.sofabird.protocol.model.YouTubeState
import app.sofabird.ui.connection.ConnectionActions
import app.sofabird.ui.connection.ConnectionContent
import app.sofabird.ui.connection.ConnectionUiState
import app.sofabird.ui.connection.NearbyPc
import app.sofabird.ui.history.DateGroup
import app.sofabird.ui.history.HistoryActions
import app.sofabird.ui.history.HistoryContent
import app.sofabird.ui.history.HistoryGroup
import app.sofabird.ui.history.HistoryUiState
import app.sofabird.ui.history.continueCards
import app.sofabird.ui.library.Crumb
import app.sofabird.ui.library.LibraryActions
import app.sofabird.ui.library.LibraryContent
import app.sofabird.ui.library.LibraryUiState
import app.sofabird.ui.media.LibraryViewSwitch
import app.sofabird.ui.media.MediaActions
import app.sofabird.ui.media.MediaContent
import app.sofabird.ui.media.MediaUiState
import app.sofabird.ui.media.MovieDetails
import app.sofabird.ui.media.ShowActions
import app.sofabird.ui.media.ShowContent
import app.sofabird.ui.media.ShowUiState
import app.sofabird.ui.playlist.PlaylistActions
import app.sofabird.ui.playlist.PlaylistContent
import app.sofabird.ui.playlist.PlaylistUiState
import app.sofabird.ui.remote.RemoteActions
import app.sofabird.ui.remote.RemoteContent
import app.sofabird.ui.remote.RemoteUiState
import app.sofabird.ui.settings.SettingsContent
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
    fun remoteHome() = snapshot("remote_home") {
        RemoteContent(home, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteHomeNextEpisode() = snapshot("remote_home_next_episode") {
        RemoteContent(home, next = NextEpisode("file:///D:/Series/Severance/Severance.S01E03.mkv", "Severance.S01E03.1080p.WEB-DL.mkv"),
            actions = object : RemoteActions {})
    }

    @Test
    fun remoteHomeEmpty() = snapshot("remote_home_empty") {
        RemoteContent(home.copy(continueItems = emptyList()), next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteSleepTimer() = snapshot("remote_sleep_timer") {
        val state = playing.copy(
            state = playing.state.copy(
                system = SystemState(sleepTimer = SleepTimerState(endsAt = System.currentTimeMillis() + 23 * 60_000L, action = "sleep")),
            ),
            capabilities = Capabilities(playing.capabilities.flags + Capability.SLEEP_TIMER + Capability.POWER),
        )
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteStreaming() = snapshot("remote_streaming") {
        val state = playing.copy(
            state = playing.state.copy(
                playback = playing.state.playback.copy(state = PlaybackStatus.PAUSED),
                system = SystemState(moonlightHost = "GAMING-PC", moonlightStreaming = true),
            ),
            capabilities = Capabilities(playing.capabilities.flags + Capability.MOONLIGHT),
        )
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteYouTube() = snapshot("remote_youtube") {
        val state = playing.copy(
            state = playing.state.copy(
                playback = playing.state.playback.copy(state = PlaybackStatus.PAUSED),
                system = SystemState(volume = 40, muted = false),
                youtube = YouTubeState(
                    open = true, playing = true, videoId = null, title = "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film",
                    channel = "Blender", time = 60.0, length = 634.6,
                ),
            ),
            capabilities = Capabilities(playing.capabilities.flags + Capability.YOUTUBE + Capability.SYSTEM_VOLUME),
        )
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteYouTubeAd() = snapshot("remote_youtube_ad") {
        val state = playing.copy(
            state = playing.state.copy(
                playback = playing.state.playback.copy(state = PlaybackStatus.PAUSED),
                system = SystemState(volume = 40, muted = false),
                youtube = YouTubeState(
                    open = true, playing = true, videoId = null, title = "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film",
                    channel = "Blender", time = 7.0, length = 15.0, ad = true, adSkippable = true,
                ),
            ),
            capabilities = Capabilities(playing.capabilities.flags + Capability.YOUTUBE + Capability.SYSTEM_VOLUME),
        )
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteOffline() = snapshot("remote_offline") {
        val state = RemoteUiState(
            connection = ConnectionState.Failed(ConnectionError.Timeout), hostName = "LIVING-ROOM-PC",
            wake = PcWaker.State.IDLE,
        )
        RemoteContent(state, next = null, actions = object : RemoteActions {})
    }

    @Test
    fun remoteWakeFailed() = snapshot("remote_wake_failed") {
        val state = RemoteUiState(
            connection = ConnectionState.Failed(ConnectionError.Timeout), hostName = "LIVING-ROOM-PC",
            wake = PcWaker.State.FAILED,
        )
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

    @Test
    fun postersFolderMissing() = snapshot("posters_missing") {
        val missing = MediaUiState(available = true, loaded = true, missingFolders = listOf("\\\\192.168.0.43\\shared\\"))
        MediaContent(missing, object : MediaActions {}, onOpenShow = {}, onPlayed = {}) {
            LibraryViewSwitch(posters = true, onChange = {})
        }
    }

    @Test
    fun posters() = snapshot("posters") {
        MediaContent(posters, object : MediaActions {}, onOpenShow = {}, onPlayed = {}) {
            LibraryViewSwitch(posters = true, onChange = {})
        }
    }

    @Test
    fun movie() = snapshot("movie") {
        MovieDetails(posters.movies[0], onPlay = {})
    }

    @Test
    fun show() = snapshot("show") {
        ShowContent(show, object : ShowActions {}, onBack = {}, onPlayed = {})
    }

    private fun snapshot(name: String, content: @Composable () -> Unit) {
        compose.setContent {
            SofabirdTheme(AppSettings()) { Surface { content() } }
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
            connection = ConnectionState.Connected("LIVING-ROOM-PC"),
            capabilities = Capabilities(setOf(Capability.CURRENT_TRACK, Capability.CHAPTER_NAMES, Capability.LAUNCH_VLC)),
            hostName = "LIVING-ROOM-PC",
        )

        /** VLC closed, a history with a file, a YouTube video and a next episode; YouTube and Moonlight on the PC. */
        private val home = playing.copy(
            state = PlayerState(
                vlc = VlcInfo(running = false, level = 2, status = VlcProcessStatus.NOT_RUNNING),
                system = SystemState(moonlightHost = "GAMING-PC"),
            ),
            capabilities = Capabilities(
                playing.capabilities.flags + Capability.HISTORY + Capability.YOUTUBE + Capability.MOONLIGHT + Capability.WINDOW_CONTROL,
            ),
            continueItems = listOf(
                HistoryItem(
                    "file:///D:/Movies/lotr.mkv", title = "The Lord of the Rings: The Fellowship of the Ring",
                    position = 2246, length = 10705, progress = 0.21,
                ),
                HistoryItem(
                    "https://www.youtube.com/watch?v=aqz-KE-bpKQ", title = "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film",
                    position = 200, length = 634, progress = 0.32, kind = HistoryItem.KIND_YOUTUBE, channel = "Blender",
                ),
                HistoryItem("file:///D:/Series/Severance/Severance.S01E03.mkv", name = "Severance.S01E03.1080p.WEB-DL.mkv", next = true),
                HistoryItem("file:///D:/Movies/matrix.mkv", title = "The Matrix", position = 600, length = 8160, progress = 0.07),
            ),
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
        private val video = HistoryItem(
            "https://www.youtube.com/watch?v=aqz-KE-bpKQ", title = "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film",
            position = 125, length = 634, kind = HistoryItem.KIND_YOUTUBE, channel = "Blender",
        )

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
            continueCards = continueCards(listOf(lotr, video)),
        )

        private val history = HistoryUiState(
            available = true,
            online = true,
            items = listOf(lotr, video, episode),
            groups = listOf(HistoryGroup(DateGroup.TODAY, listOf(lotr, video)), HistoryGroup(DateGroup.THIS_WEEK, listOf(episode))),
            total = 3,
        )

        private fun file(name: String, progress: Double? = null, finished: Boolean? = null) =
            MediaFile("file:///D:/Media/$name", name, size = 4_500_000_000, progress = progress, finished = finished)

        private val posters = MediaUiState(
            available = true,
            loaded = true,
            movies = listOf(
                LibraryMovie(
                    "lotr|2001", "The Lord of the Rings: The Fellowship of the Ring", 2001,
                    overview = "Young hobbit Frodo Baggins, after inheriting a mysterious ring from his uncle Bilbo, must leave " +
                        "his home in order to keep it from falling into the hands of its evil creator.",
                    rating = 8.4,
                    files = listOf(file("lotr.2160p.mkv", progress = 0.21), file("lotr.1080p.mkv")),
                ),
                LibraryMovie("matrix|1999", "The Matrix", 1999, files = listOf(file("matrix.mkv", finished = true))),
                LibraryMovie("unknown film|2020", "Unknown.Film.2020.WEB-DL", 2020, files = listOf(file("unknown.mkv"))),
            ),
            shows = listOf(
                LibraryShow("severance", "Severance", 2022, episodes = 9, unwatched = 7),
                LibraryShow("expanse", "The Expanse", 2015, episodes = 10, unwatched = 0),
            ),
        )

        private val show = ShowUiState(
            title = "Severance",
            details = ShowDetails(
                LibraryShow(
                    "severance", "Severance", 2022,
                    overview = "Mark leads a team of office workers whose memories have been surgically divided between " +
                        "their work and personal lives.",
                    episodes = 3, unwatched = 2,
                ),
                listOf(
                    LibraryEpisode(1, 1, "Good News About Hell", file = file("S01E01.mkv", progress = 1.0, finished = true)),
                    LibraryEpisode(1, 2, "Half Loop", file = file("S01E02.mkv", progress = 0.4)),
                    LibraryEpisode(2, 1, null, file = file("Severance.S02E01.1080p.WEB-DL.mkv")),
                ),
            ),
        )

        private val savedPc = SavedServer(
            id = "pc", name = "LIVING-ROOM-PC", host = "192.168.1.17", pcId = "guid-1",
        )
        private val connection = ConnectionUiState(
            nearby = listOf(
                NearbyPc(
                    DiscoveredHost("192.168.1.17", "LIVING-ROOM-PC", vlcStatus = VlcProcessStatus.RUNNING),
                    saved = savedPc,
                    paired = true,
                ),
            ),
            scanning = true,
            saved = listOf(savedPc.copy(id = "old", name = "Office PC", host = "192.168.1.30", pcId = "guid-2")),
            activeServerId = savedPc.id,
            activeServer = savedPc,
            connection = ConnectionState.Connected("LIVING-ROOM-PC"),
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
