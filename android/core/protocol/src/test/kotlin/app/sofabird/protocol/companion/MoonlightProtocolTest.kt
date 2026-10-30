package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.MoonlightShortcut
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.MoonlightHost
import org.junit.Assert.assertEquals
import org.junit.Test

class MoonlightProtocolTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    @Test
    fun `the hosts request asks for a refresh only when wanted`() {
        assertEquals(
            """{"t":"sys","id":"1","name":"moonlightHosts","args":{}}""",
            CompanionCodec.encode(CommandMapping.toSys("1", SystemCommand.MoonlightHosts())),
        )
        assertEquals(
            """{"t":"sys","id":"2","name":"moonlightHosts","args":{"refresh":true}}""",
            CompanionCodec.encode(CommandMapping.toSys("2", SystemCommand.MoonlightHosts(refresh = true))),
        )
    }

    @Test
    fun `the reply lists the computers with their apps`() {
        val reply = known(
            """{"t":"moonlightHosts","id":"9","items":[{"name":"GAMING-PC","apps":["Desktop","Steam Big Picture"]},""" +
                """{"name":"office-pc","apps":[],"stale":true},{"name":"new-pc","future":1}]}""",
        ) as CompanionMessage.MoonlightHosts
        assertEquals("9", reply.id)
        assertEquals(
            listOf(
                MoonlightHost("GAMING-PC", listOf("Desktop", "Steam Big Picture")),
                MoonlightHost("office-pc", emptyList(), stale = true),
                MoonlightHost("new-pc"),
            ),
            reply.items,
        )
    }

    @Test
    fun `a computer whose apps are unknown still offers its desktop`() {
        assertEquals(listOf("Desktop"), MoonlightHost("office-pc").choices)
        assertEquals(listOf("Desktop", "Steam Big Picture"), MoonlightHost("GAMING-PC", listOf("Desktop", "Steam Big Picture")).choices)
    }

    @Test
    fun `moonlight shortcuts are ctrl alt shift with a letter`() {
        assertEquals(
            """{"t":"input","kind":"key","key":"s","mods":["ctrl","alt","shift"]}""",
            CompanionCodec.encode(CommandMapping.toInput(MoonlightShortcut.STATS.input)),
        )
        assertEquals(
            listOf("s", "m", "d", "q"),
            MoonlightShortcut.entries.map { CommandMapping.toInput(it.input).key },
        )
    }
}
