package app.sofabird.protocol.model

import kotlinx.serialization.Serializable

/*
 * The library of the PC's media folders (capability `library`, protocol/README.md "Library"): movies and series with
 * TMDB metadata, built by the companion. Images are full TMDB URLs the phone loads itself.
 */

/** `state.library`: [revision] changes when files or metadata change; [scanning] while the companion works on it. */
@Serializable
data class LibraryInfo(
    val revision: Long = 0,
    val scanning: Boolean = false,
)

/** A video file of the library, with what the watch history knows about it. */
@Serializable
data class MediaFile(
    val uri: String,
    val name: String = "",
    val size: Long = 0,
    /** Watched fraction 0..1; `null` = never played. */
    val progress: Double? = null,
    val finished: Boolean? = null,
)

@Serializable
data class LibraryMovie(
    val key: String,
    val title: String,
    val year: Int? = null,
    val overview: String? = null,
    val poster: String? = null,
    val backdrop: String? = null,
    val rating: Double? = null,
    /** Usually one; several are versions of the same movie, the biggest first. */
    val files: List<MediaFile> = emptyList(),
)

/** A series in the `library` list; its episodes come with `getShow`. */
@Serializable
data class LibraryShow(
    val key: String,
    val title: String,
    val year: Int? = null,
    val overview: String? = null,
    val poster: String? = null,
    val backdrop: String? = null,
    val rating: Double? = null,
    val episodes: Int = 0,
    /** Episodes not finished yet. */
    val unwatched: Int = 0,
)

@Serializable
data class LibraryEpisode(
    val season: Int,
    val episode: Int,
    /** TMDB's name of the episode; `null` when unknown. */
    val title: String? = null,
    val overview: String? = null,
    val still: String? = null,
    val file: MediaFile,
)

/** Answer to `getLibrary`. */
data class Library(
    val revision: Long = 0,
    val scanning: Boolean = false,
    val movies: List<LibraryMovie> = emptyList(),
    val shows: List<LibraryShow> = emptyList(),
    /** Media folders the PC cannot reach now (a disconnected disk, a network folder that is off). */
    val missingFolders: List<String> = emptyList(),
)

/** Answer to `getShow`. */
data class ShowDetails(
    val show: LibraryShow,
    val episodes: List<LibraryEpisode>,
)
