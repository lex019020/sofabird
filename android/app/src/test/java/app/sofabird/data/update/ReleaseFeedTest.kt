package app.sofabird.data.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ReleaseFeedTest {

    private fun v(text: String) = AppVersion.parse(text)!!

    @Test
    fun `versions follow semver`() {
        assertTrue(v("0.4.0") > v("0.4.0-beta0064"))
        assertTrue(v("0.4.0-beta0065") > v("0.4.0-beta0064"))
        assertTrue(v("0.5.0-beta0070") > v("0.4.0"))
        assertTrue(v("v0.4.1") > v("0.4.0"))
        assertTrue(v("1.0.0") > v("0.10.0"))
        assertEquals(v("v0.4.0"), v("0.4.0"))
        assertEquals("0.4.0-beta0064", v("v0.4.0-beta0064").toString())
        assertNull(AppVersion.parse("0.4"))
        assertNull(AppVersion.parse("latest"))
    }

    @Test
    fun `a test build follows test builds, a release follows releases`() {
        assertEquals(UpdateChannel.TEST, UpdateChannel.of(v("0.4.0-beta0064")))
        assertEquals(UpdateChannel.STABLE, UpdateChannel.of(v("0.4.0")))
    }

    @Test
    fun `the newest build of the channel with an apk`() {
        val releases = ReleaseFeed.parse(FEED)
        val test = ReleaseFeed.newest(releases, UpdateChannel.TEST, v("0.4.0-beta0063"))!!
        assertEquals("0.4.0-beta0066", test.version)
        assertEquals("https://example.test/Sofabird-0.4.0-beta0066.apk", test.apkUrl)
        assertEquals(17_361_198L, test.size)
        assertEquals("https://github.com/lex019020/sofabird/releases/tag/v0.4.0-beta0066", test.page)

        // A release has no test builds after it here: nothing newer.
        assertNull(ReleaseFeed.newest(releases, UpdateChannel.STABLE, v("0.3.0")))
        assertEquals("0.3.0", ReleaseFeed.newest(releases, UpdateChannel.STABLE, v("0.2.0"))!!.version)
        assertNull(ReleaseFeed.newest(releases, UpdateChannel.TEST, v("0.4.0-beta0066")))
    }

    @Test
    fun `docs-only builds are offered but do not ask`() {
        val releases = ReleaseFeed.parse(FEED)
        // 0065 (no app changes) and 0066 (no app changes) since 0064: offered, not noteworthy.
        assertFalse(ReleaseFeed.newest(releases, UpdateChannel.TEST, v("0.4.0-beta0064"))!!.noteworthy)
        // 0064 is noteworthy: an update from 0063 asks.
        assertTrue(ReleaseFeed.newest(releases, UpdateChannel.TEST, v("0.4.0-beta0063"))!!.noteworthy)
    }

    @Test
    fun `drafts and releases without the apk are skipped`() {
        val releases = ReleaseFeed.parse(
            """[
              {"tag_name":"v0.5.0","draft":true,"assets":[{"name":"Sofabird-0.5.0.apk","browser_download_url":"x"}]},
              {"tag_name":"v0.4.1","assets":[{"name":"Sofabird-Setup.exe","browser_download_url":"x"}]}
            ]""",
        )
        assertNull(ReleaseFeed.newest(releases, UpdateChannel.STABLE, v("0.4.0")))
    }

    private companion object {
        val FEED = """
            [
              {"tag_name":"v0.4.0-beta0066","prerelease":true,"draft":false,
               "html_url":"https://github.com/lex019020/sofabird/releases/tag/v0.4.0-beta0066",
               "body":"No user-visible changes.\n\n---\n### Download",
               "assets":[{"name":"Sofabird-Setup.exe","size":1,"browser_download_url":"https://example.test/Sofabird-Setup.exe"},
                         {"name":"Sofabird-0.4.0-beta0066.apk","size":17361198,"browser_download_url":"https://example.test/Sofabird-0.4.0-beta0066.apk"}]},
              {"tag_name":"v0.4.0-beta0065","prerelease":true,"body":"No user-visible changes.",
               "assets":[{"name":"Sofabird-0.4.0-beta0065.apk","size":1,"browser_download_url":"https://example.test/65.apk"}]},
              {"tag_name":"v0.4.0-beta0064","prerelease":true,"body":"### Fixes\n- Series numbered in Russian words show up as a series.",
               "assets":[{"name":"Sofabird-0.4.0-beta0064.apk","size":1,"browser_download_url":"https://example.test/64.apk"}]},
              {"tag_name":"v0.3.0","prerelease":false,"body":"### New\n- The library.",
               "assets":[{"name":"Sofabird-0.3.0.apk","size":1,"browser_download_url":"https://example.test/30.apk"}],
               "unknown_field":{"ignored":true}}
            ]
        """.trimIndent()
    }
}
