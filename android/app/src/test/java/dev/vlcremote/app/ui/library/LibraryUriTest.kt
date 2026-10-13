package dev.vlcremote.app.ui.library

import org.junit.Assert.assertEquals
import org.junit.Test

class LibraryUriTest {
    @Test
    fun `local folders go up to the drive and then to the root`() {
        assertEquals("file:///D:/shared/", parentDirUri("file:///D:/shared/Movies%202001/"))
        assertEquals("file:///D:/", parentDirUri("file:///D:/shared/"))
        assertEquals(ROOT_URI, parentDirUri("file:///D:/"))
        assertEquals(ROOT_URI, parentDirUri(ROOT_URI))
    }

    @Test
    fun `network folders stop at the share root`() {
        assertEquals("file://192.168.0.43/shared/", parentDirUri("file://192.168.0.43/shared/Movies/"))
        assertEquals(ROOT_URI, parentDirUri("file://192.168.0.43/shared/"))
    }

    @Test
    fun `crumbs for local and network paths`() {
        assertEquals(listOf("file:///D:/", "file:///D:/a/", "file:///D:/a/b/"), crumbUris("file:///D:/a/b/"))
        assertEquals(listOf("file://nas/video/", "file://nas/video/films/"), crumbUris("file://nas/video/films/"))
        assertEquals(emptyList<String>(), crumbUris(ROOT_URI))
    }
}
