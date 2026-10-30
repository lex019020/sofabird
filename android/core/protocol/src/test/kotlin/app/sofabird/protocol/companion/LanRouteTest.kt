package app.sofabird.protocol.companion

import org.junit.Assert.assertEquals
import org.junit.Test

class LanRouteTest {

    @Test
    fun `a pinned PC is reached on its encrypted port with the pin`() {
        assertEquals(LanRoute(47557, pin = "AB"), lanRoute(47557, "AB"))
        assertEquals(LanRoute(47600, pin = "AB"), lanRoute(47600, "AB"))
    }

    @Test
    fun `without a pin yet - trust on first use`() {
        assertEquals(LanRoute(47600), lanRoute(tlsPort = 47600, pin = null))
    }

    @Test
    fun `port not known yet - the default encrypted port`() {
        assertEquals(LanRoute(COMPANION_TLS_PORT), lanRoute(tlsPort = null, pin = null))
        assertEquals(LanRoute(47557, pin = "AB"), lanRoute(tlsPort = null, pin = "AB"))
    }

    @Test
    fun `artwork is fetched from the host the link was opened to`() {
        assertEquals(
            "https://192.168.0.10:47557/art?plId=5",
            artViaLink("https://192.168.0.17:47557/art?plId=5", "192.168.0.10", 47557),
        )
        assertEquals("https://[fe80::1]:47557/art?plId=5", artViaLink("https://192.168.0.17:47557/art?plId=5", "fe80::1", 47557))
        assertEquals("https://pc.local:47557/art", artViaLink("https://192.168.0.17:47557/art", "pc.local", 47557))
    }

    @Test
    fun `other URLs stay as they are`() {
        assertEquals("https://image.tmdb.org/t/p/w500/x.jpg", artViaLink("https://image.tmdb.org/t/p/w500/x.jpg", "h", 47557))
        assertEquals("https://i.ytimg.com/vi/x/hqdefault.jpg", artViaLink("https://i.ytimg.com/vi/x/hqdefault.jpg", "h", 47557))
        assertEquals("https://192.168.0.17:8443/art?plId=1", artViaLink("https://192.168.0.17:8443/art?plId=1", "h", 47557))
        assertEquals("https://192.168.0.17:47557/info", artViaLink("https://192.168.0.17:47557/info", "h", 47557))
        assertEquals("https://192.168.0.17:47557/artist", artViaLink("https://192.168.0.17:47557/artist", "h", 47557))
        assertEquals("http://192.168.0.17:47557/art?plId=1", artViaLink("http://192.168.0.17:47557/art?plId=1", "h", 47557))
        assertEquals(null, artViaLink(null, "h", 47557))
    }

    @Test
    fun `welcome with tls decodes`() {
        val decoded = CompanionCodec.decode(
            """{"t":"welcome","v":1,"name":"HTPC","id":"pc","caps":[],"tls":{"port":47557,"sha256":"AB12"}}""",
        )
        val welcome = (decoded as DecodedMessage.Known).message as CompanionMessage.Welcome
        assertEquals(TlsInfo(47557, "AB12"), welcome.tls)
    }
}
