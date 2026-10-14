package app.sofabird.protocol.vlc

import app.sofabird.protocol.model.Chapters
import app.sofabird.protocol.model.Delays
import app.sofabird.protocol.model.Flags
import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlaybackStatus
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlayingItem
import app.sofabird.protocol.model.Titles
import app.sofabird.protocol.model.Track
import app.sofabird.protocol.model.TrackKind
import app.sofabird.protocol.model.Tracks
import app.sofabird.protocol.model.VideoState
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.model.Volume

/** Current track ids remembered on level 0 (VLC does not report them). */
data class RememberedTracks(
    val audio: Int? = null,
    val subtitle: Int? = null,
    val video: Int? = null,
) {
    fun with(kind: TrackKind, id: Int?): RememberedTracks = when (kind) {
        TrackKind.AUDIO -> copy(audio = id)
        TrackKind.SUBTITLE -> copy(subtitle = id)
        TrackKind.VIDEO -> copy(video = id)
    }

    companion object {
        val NONE = RememberedTracks()
    }
}

/** Builds the unified [PlayerState] from direct-mode VLC responses. */
object VlcStateMapper {

    fun map(
        status: VlcStatus,
        tracksPage: VlcTracksPage?,
        remembered: RememberedTracks,
        level: Int,
        /** Absolute URL of `/art` on this server (with auth handled by the HTTP layer), or null. */
        artUrl: String?,
        muted: Boolean,
        nowMs: Long,
        previous: PlayerState? = null,
    ): PlayerState {
        val playback = PlaybackStatus.fromWire(status.state)
        val info = status.information
        val streams = info?.streams.orEmpty()

        val tracks = buildTracks(streams, tracksPage, remembered)
        val chapters = buildChapters(info, tracksPage)
        val titles = Titles(
            index = info?.title ?: -1,
            count = tracksPage?.title?.items?.size ?: info?.titles?.size ?: 0,
        )
        val hasItem = status.currentPlId >= 0 && playback != PlaybackStatus.STOPPED
        val item = if (hasItem) {
            PlayingItem(
                plId = status.currentPlId,
                uri = null,
                title = info?.meta?.title ?: info?.meta?.filename,
                artUrl = if (info?.meta?.artworkUrl != null) artUrl else null,
            )
        } else {
            // Keep showing the last item after stop ("Now Playing stays after stop").
            previous?.item ?: PlayingItem()
        }

        return PlayerState(
            vlc = VlcInfo(running = true, version = status.version, apiVersion = status.apiVersion, level = level),
            playback = Playback(
                state = playback,
                time = status.time,
                length = status.length,
                position = status.position,
                rate = status.rate,
                updatedAt = nowMs,
            ),
            item = item,
            tracks = tracks,
            chapters = chapters,
            titles = titles,
            volume = Volume(value = status.volume, muted = muted),
            video = VideoState(fullscreen = status.fullscreen, aspect = status.aspectRatio),
            delays = Delays(audio = status.audioDelay, subtitle = status.subtitleDelay),
            flags = Flags(random = status.random, loop = status.loop, repeat = status.repeat),
            system = null,
        )
    }

    private fun buildTracks(streams: List<VlcStream>, page: VlcTracksPage?, remembered: RememberedTracks): Tracks {
        val fromStatus = streams.groupBy { it.type }
        fun fromStreams(type: VlcStreamType) = fromStatus[type].orEmpty().map { s ->
            Track(id = s.id, name = s.description ?: "#${s.id}", lang = s.language, codec = s.codec)
        }

        val audio = mergeWithPage(fromStreams(VlcStreamType.AUDIO), page?.audio)
        val subtitle = mergeWithPage(fromStreams(VlcStreamType.SUBTITLE), page?.spu)
        val video = mergeWithPage(fromStreams(VlcStreamType.VIDEO), page?.video)

        return Tracks(
            audio = audio,
            subtitle = subtitle,
            video = video,
            currentAudio = page?.audio?.currentInt ?: remembered.audio,
            currentSubtitle = page?.spu?.currentInt ?: remembered.subtitle,
            currentVideo = page?.video?.currentInt ?: remembered.video,
        )
    }

    /**
     * Status streams carry codec/language; the Lua page carries the authoritative id list and
     * the same names with ` - [Language]`. Prefer the page's set of ids, enrich from status.
     */
    private fun mergeWithPage(fromStatus: List<Track>, page: VlcSelectList?): List<Track> {
        if (page == null) return fromStatus
        val byId = fromStatus.associateBy { it.id }
        val merged = page.items.mapNotNull { item ->
            val id = item.intId ?: return@mapNotNull null
            if (id == -1) return@mapNotNull null // "Disable" is rendered by the UI itself
            val (name, lang) = VlcStatusParser.splitTrackName(item.name)
            val known = byId[id]
            Track(
                id = id,
                name = known?.name ?: name,
                lang = known?.lang ?: lang,
                codec = known?.codec,
            )
        }
        return merged.ifEmpty { fromStatus }
    }

    private fun buildChapters(info: VlcInformation?, page: VlcTracksPage?): Chapters {
        val pageChapters = page?.chapter
        val names = pageChapters?.items?.takeIf { it.isNotEmpty() }?.map { it.name.trim() }
        val count = names?.size ?: info?.chapters?.size ?: 0
        val index = pageChapters?.currentInt ?: info?.chapter ?: -1
        return Chapters(index = index, count = count, names = names)
    }
}
