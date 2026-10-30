package app.sofabird.ui.remote

import app.sofabird.domain.model.MoonlightChoice
import app.sofabird.domain.model.SavedServer
import app.sofabird.protocol.companion.CompanionErrorException
import app.sofabird.protocol.companion.ErrorCode
import app.sofabird.protocol.model.MoonlightHost
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test

class MoonlightUiTest {

    private val gaming = MoonlightHost("GAMING-PC", listOf("Desktop", "Steam Big Picture", "Cyberpunk 2077"))
    private val office = MoonlightHost("office-pc")
    private val hosts = listOf(gaming, office)

    @Test
    fun `nothing paired - nothing chosen`() {
        val ui = MoonlightUi(hosts = emptyList(), last = MoonlightChoice("GAMING-PC", "Desktop"))
        assertNull(ui.host)
        assertNull(ui.app)
    }

    @Test
    fun `by default the first computer and its desktop`() {
        val ui = MoonlightUi(hosts = listOf(MoonlightHost("pc", listOf("Steam Big Picture", "Desktop"))))
        assertEquals("pc", ui.host?.name)
        assertEquals("Desktop", ui.app)
        // No Desktop among the apps: the first one.
        assertEquals("Game", MoonlightUi(hosts = listOf(MoonlightHost("pc", listOf("Game")))).app)
    }

    @Test
    fun `the last choice of this phone is preselected`() {
        val ui = MoonlightUi(hosts = hosts, last = MoonlightChoice("OFFICE-PC", "Desktop"))
        assertEquals(office, ui.host)
        assertEquals("Desktop", ui.app) // its apps are unknown: Desktop all the same

        val game = MoonlightUi(hosts = hosts, last = MoonlightChoice("GAMING-PC", "cyberpunk 2077"))
        assertEquals("Cyberpunk 2077", game.app)

        // The remembered app is gone from the computer: Desktop.
        assertEquals("Desktop", MoonlightUi(hosts = hosts, last = MoonlightChoice("GAMING-PC", "Uninstalled")).app)
        // The remembered computer is no longer paired: the first one.
        assertEquals(gaming, MoonlightUi(hosts = hosts, last = MoonlightChoice("old-pc", "Game")).host)
    }

    @Test
    fun `a pick in the sheet wins over the remembered choice`() {
        val last = MoonlightChoice("GAMING-PC", "Cyberpunk 2077")
        // Another computer picked: its own default, not the remembered app of the other one.
        assertEquals(office to "Desktop", MoonlightUi(hosts = hosts, pickedHost = "office-pc", last = last).let { it.host to it.app })
        // The remembered computer picked again: its remembered app.
        assertEquals("Cyberpunk 2077", MoonlightUi(hosts = hosts, pickedHost = "GAMING-PC", last = last).app)
        assertEquals("Steam Big Picture", MoonlightUi(hosts = hosts, pickedHost = "GAMING-PC", pickedApp = "Steam Big Picture", last = last).app)
    }

    @Test
    fun `the list arrives, or the failure is shown`() {
        val loading = MoonlightUi(loading = true, refreshing = true, error = IllegalStateException("old"))
        val ok = loading.loaded(Result.success(hosts), stateHost = "GAMING-PC")
        assertEquals(MoonlightUi(hosts = hosts, loaded = true), ok)

        val timeout = IllegalStateException("not connected")
        val failed = ok.copy(loading = true).loaded(Result.failure(timeout), stateHost = "GAMING-PC")
        assertEquals(hosts, failed.hosts) // the last list stays
        assertEquals(timeout, failed.error)
        assertFalse(failed.loading)
    }

    @Test
    fun `a companion without the list still offers its first computer's desktop`() {
        val unsupported = CompanionErrorException(ErrorCode.UNSUPPORTED, "unsupported", "unknown sys command 'moonlightHosts'")
        val ui = MoonlightUi(loading = true).loaded(Result.failure(unsupported), stateHost = "GAMING-PC")
        assertEquals(listOf(MoonlightHost("GAMING-PC")), ui.hosts)
        assertNull(ui.error)
        assertEquals("Desktop", ui.app)
        // Without a computer in the state there is nothing to offer: the error shows.
        assertEquals(unsupported, MoonlightUi().loaded(Result.failure(unsupported), stateHost = null).error)
    }

    @Test
    fun `the choice is kept with the saved PC, and older saved lists still read`() {
        val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
        val saved = SavedServer(id = "pc", name = "LIVING-ROOM-PC", host = "192.168.1.17", moonlight = MoonlightChoice("GAMING-PC", "Desktop"))
        assertEquals(listOf(saved), SavedServer.decodeList(json, SavedServer.encodeList(json, listOf(saved))))
        val old = SavedServer.decodeList(json, """[{"id":"pc","name":"LIVING-ROOM-PC","host":"192.168.1.17"}]""").single()
        assertNull(old.moonlight)
    }
}
