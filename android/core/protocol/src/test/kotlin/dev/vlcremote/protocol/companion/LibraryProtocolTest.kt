package dev.vlcremote.protocol.companion

import dev.vlcremote.protocol.model.Capabilities
import dev.vlcremote.protocol.model.LibraryInfo
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The library (protocol/README.md "Library"). */
class LibraryProtocolTest {

    private fun known(text: String): CompanionMessage = (CompanionCodec.decode(text) as DecodedMessage.Known).message

    private fun obj(message: CompanionMessage): JsonObject =
        CompanionCodec.json.parseToJsonElement(CompanionCodec.encode(message)) as JsonObject

    @Test
    fun `library capability and state block are parsed`() {
        assertTrue(Capabilities.fromWire(listOf("library")).library)
        val state = known("""{"t":"state","library":{"revision":12,"scanning":true}}""") as CompanionMessage.State
        assertEquals(LibraryInfo(12, scanning = true), state.toPlayerState().library)
        assertNull((known("""{"t":"state"}""") as CompanionMessage.State).toPlayerState().library)
    }

    @Test
    fun `requests are encoded`() {
        assertEquals("getLibrary", obj(CompanionMessage.GetLibrary("5"))["t"]!!.jsonPrimitive.content)
        val show = obj(CompanionMessage.GetShow("6", "severance"))
        assertEquals("getShow", show["t"]!!.jsonPrimitive.content)
        assertEquals("severance", show["key"]!!.jsonPrimitive.content)
    }

    @Test
    fun `library reply is decoded, absent fields stay null`() {
        val library = (
            known(
                """{"t":"library","id":"5","revision":12,"scanning":false,
                   "movies":[{"key":"the matrix|1999","title":"Матрица","year":1999,"overview":"…",
                     "poster":"https://image.tmdb.org/t/p/w342/m.jpg","rating":8.2,
                     "files":[{"uri":"file:///D:/Movies/The.Matrix.1999.mkv","name":"The.Matrix.1999.mkv","size":10,"progress":0.4}]},
                     {"key":"unknown film|2020","title":"Unknown Film","year":2020,"files":[]}],
                   "shows":[{"key":"severance","title":"Разделение","episodes":9,"unwatched":7}]}""",
            ) as CompanionMessage.Library
            ).toLibrary()
        assertEquals(12L, library.revision)
        val matrix = library.movies[0]
        assertEquals("Матрица", matrix.title)
        assertEquals(0.4, matrix.files.single().progress!!, 0.0)
        assertNull(matrix.files.single().finished)
        assertNull(library.movies[1].poster)
        assertEquals(7, library.shows.single().unwatched)
    }

    @Test
    fun `show reply is decoded`() {
        val details = (
            known(
                """{"t":"show","id":"6","show":{"key":"severance","title":"Разделение","episodes":2,"unwatched":1},
                   "episodes":[{"season":1,"episode":1,"title":"Хорошие новости","still":"https://image.tmdb.org/t/p/w300/e.jpg",
                     "file":{"uri":"file:///D:/S/S01E01.mkv","name":"S01E01.mkv","size":1,"progress":1.0,"finished":true}},
                     {"season":1,"episode":2,"file":{"uri":"file:///D:/S/S01E02.mkv","name":"S01E02.mkv","size":1}}]}""",
            ) as CompanionMessage.Show
            ).toDetails()
        assertEquals("Разделение", details.show.title)
        assertEquals(listOf("Хорошие новости", null), details.episodes.map { it.title })
        assertEquals(true, details.episodes[0].file.finished)
    }
}
