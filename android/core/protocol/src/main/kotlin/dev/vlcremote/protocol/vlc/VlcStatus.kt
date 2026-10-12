package dev.vlcremote.protocol.vlc

/** One `Stream N` category of `status.json` → `information.category`. */
data class VlcStream(
    /** Id to pass to `audio_track`/`subtitle_track`/`video_track`: `Original_ID` if present, else N. */
    val id: Int,
    /** `Video` / `Audio` / `Subtitle` (as VLC reports it, possibly localized elsewhere). */
    val type: VlcStreamType,
    val language: String? = null,
    val description: String? = null,
    val codec: String? = null,
)

enum class VlcStreamType { VIDEO, AUDIO, SUBTITLE, OTHER;

    companion object {
        fun parse(raw: String?): VlcStreamType = when (raw?.trim()?.lowercase()) {
            "video", "видео" -> VIDEO
            "audio", "аудио" -> AUDIO
            "subtitle", "subtitles", "субтитры" -> SUBTITLE
            else -> OTHER
        }
    }
}

data class VlcMeta(
    val title: String? = null,
    val filename: String? = null,
    val artworkUrl: String? = null,
    val artist: String? = null,
    val album: String? = null,
)

/** `information` object; absent entirely in `stopped`. */
data class VlcInformation(
    val chapter: Int = -1,
    val title: Int = -1,
    val chapters: List<Int> = emptyList(),
    val titles: List<Int> = emptyList(),
    val meta: VlcMeta = VlcMeta(),
    val streams: List<VlcStream> = emptyList(),
)

/** Typed `status.json` (apiversion 3). */
data class VlcStatus(
    val state: String,
    val time: Long,
    val length: Long,
    val position: Double,
    val rate: Double,
    val volume: Int,
    val fullscreen: Boolean,
    val currentPlId: Int,
    val audioDelay: Double,
    val subtitleDelay: Double,
    val aspectRatio: String,
    val random: Boolean,
    val loop: Boolean,
    val repeat: Boolean,
    val apiVersion: Int,
    val version: String?,
    val seekSec: Int,
    val information: VlcInformation?,
)

/** One selectable item of the level-1 `tracks.json` page. */
data class VlcSelectItem(
    /** Numeric id for tracks/chapters; string for aspect/crop. */
    val id: String,
    val name: String,
) {
    val intId: Int? get() = id.toDoubleOrNull()?.toInt()
}

data class VlcSelectList(
    val current: String?,
    val items: List<VlcSelectItem>,
) {
    val currentInt: Int? get() = current?.toDoubleOrNull()?.toInt()
}

/** `GET /requests/tracks.json` (our Lua page). Every list may be `null` when VLC has no input. */
data class VlcTracksPage(
    val audio: VlcSelectList? = null,
    val spu: VlcSelectList? = null,
    val video: VlcSelectList? = null,
    val chapter: VlcSelectList? = null,
    val title: VlcSelectList? = null,
    val aspect: VlcSelectList? = null,
    val crop: VlcSelectList? = null,
    val zoom: VlcSelectList? = null,
    val deinterlace: VlcSelectList? = null,
    val stereoMode: VlcSelectList? = null,
)
