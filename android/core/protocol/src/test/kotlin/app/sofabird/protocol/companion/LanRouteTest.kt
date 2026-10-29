package app.sofabird.protocol.companion

import org.junit.Assert.assertEquals
import org.junit.Test

class LanRouteTest {

    @Test
    fun `a pinned PC is reached only encrypted`() {
        assertEquals(listOf(LanRoute(47557, tls = true, pin = "AB")), lanRoutes(47555, 47557, "AB"))
        assertEquals(listOf(LanRoute(47600, tls = true, pin = "AB")), lanRoutes(47555, 47600, "AB"))
    }

    @Test
    fun `known to have tls but not pinned yet - encrypted first then plain`() {
        assertEquals(
            listOf(LanRoute(47557, tls = true), LanRoute(47555, tls = false)),
            lanRoutes(47555, tlsPort = 47557, pin = null),
        )
    }

    @Test
    fun `not known to have tls - plain, the welcome tells`() {
        assertEquals(listOf(LanRoute(47555, tls = false)), lanRoutes(47555, tlsPort = null, pin = null))
    }

    @Test
    fun `artwork goes through the encrypted listener`() {
        assertEquals(
            "https://192.168.0.10:47557/art?plId=5",
            artOverTls("http://192.168.0.17:47555/art?plId=5", "192.168.0.10", 47555, 47557),
        )
        assertEquals("https://image.tmdb.org/t/p/w500/x.jpg", artOverTls("https://image.tmdb.org/t/p/w500/x.jpg", "h", 47555, 47557))
        assertEquals("http://192.168.0.17:8080/art", artOverTls("http://192.168.0.17:8080/art", "h", 47555, 47557))
        assertEquals("http://192.168.0.17:47555/info", artOverTls("http://192.168.0.17:47555/info", "h", 47555, 47557))
        assertEquals(null, artOverTls(null, "h", 47555, 47557))
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
