package app.sofabird.protocol.model

import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder

/** Playback state as reported by VLC (`state` in status.json / `playback.state` in the protocol). */
@Serializable
enum class PlaybackStatus {
    @SerialName("playing")
    PLAYING,

    @SerialName("paused")
    PAUSED,

    @SerialName("stopped")
    STOPPED;

    val isPlaying: Boolean get() = this == PLAYING

    companion object {
        fun fromWire(value: String?): PlaybackStatus = when (value) {
            "playing" -> PLAYING
            "paused" -> PAUSED
            else -> STOPPED
        }
    }
}

@Serializable
data class VlcInfo(
    /** "Can be controlled right now" — kept for old companions; see [status] for the details. */
    val running: Boolean = false,
    val version: String? = null,
    val apiVersion: Int? = null,
    /** The companion always says 2 (levels 0/1 were the app's direct VLC mode, removed in 0.5). */
    val level: Int = 0,
    /** Companion only, optional: old companions don't send it. Use [effectiveStatus]. */
    val status: VlcProcessStatus? = null,
) {
    /**
     * [status] when the companion reported a known value, otherwise derived from [running]
     * (`running` → [VlcProcessStatus.RUNNING], else [VlcProcessStatus.NOT_RUNNING]).
     */
    val effectiveStatus: VlcProcessStatus
        get() = status?.takeIf { it != VlcProcessStatus.UNKNOWN }
            ?: if (running) VlcProcessStatus.RUNNING else VlcProcessStatus.NOT_RUNNING

    /** Same info after VLC exited: not controllable, process gone. */
    fun exited(): VlcInfo = copy(running = false, status = status?.let { VlcProcessStatus.NOT_RUNNING })
}

/**
 * `vlc.status` (protocol/README.md, "State: `state`"). Values a newer companion may add decode as
 * [UNKNOWN] instead of failing the whole message.
 */
@Serializable(with = VlcProcessStatusSerializer::class)
enum class VlcProcessStatus(val wire: String) {
    /** VLC is not installed on the PC. */
    NOT_INSTALLED("notInstalled"),

    /** No VLC process. */
    NOT_RUNNING("notRunning"),

    /** The companion is starting VLC. */
    STARTING("starting"),

    /** VLC runs, but its web interface does not accept the companion (opened by hand). */
    NO_CONTROL("noControl"),

    /** Full control. */
    RUNNING("running"),

    /** A value this app version does not know. */
    UNKNOWN("");

    companion object {
        fun fromWire(value: String?): VlcProcessStatus = entries.firstOrNull { it != UNKNOWN && it.wire == value } ?: UNKNOWN
    }
}

internal object VlcProcessStatusSerializer : KSerializer<VlcProcessStatus> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("app.sofabird.protocol.model.VlcProcessStatus", PrimitiveKind.STRING)

    override fun serialize(encoder: Encoder, value: VlcProcessStatus) = encoder.encodeString(value.wire)

    override fun deserialize(decoder: Decoder): VlcProcessStatus = VlcProcessStatus.fromWire(decoder.decodeString())
}

@Serializable
data class Playback(
    val state: PlaybackStatus = PlaybackStatus.STOPPED,
    /** Seconds. */
    val time: Long = 0,
    /** Seconds; 0 when unknown/stopped. */
    val length: Long = 0,
    /** 0..1 */
    val position: Double = 0.0,
    val rate: Double = 1.0,
    /** Wall-clock millis when `time` was sampled; used for local interpolation. */
    val updatedAt: Long = 0,
)

@Serializable
data class PlayingItem(
    val plId: Int? = null,
    val uri: String? = null,
    val title: String? = null,
    val artUrl: String? = null,
)

@Serializable
enum class TrackKind {
    @SerialName("audio")
    AUDIO,

    @SerialName("subtitle")
    SUBTITLE,

    @SerialName("video")
    VIDEO;

    val wire: String get() = when (this) {
        AUDIO -> "audio"
        SUBTITLE -> "subtitle"
        VIDEO -> "video"
    }
}

@Serializable
data class Track(
    val id: Int,
    val name: String,
    val lang: String? = null,
    val codec: String? = null,
) {
    /** Id used by VLC/companion for "disable". */
    val isDisable: Boolean get() = id == -1
}

@Serializable
data class Tracks(
    val audio: List<Track> = emptyList(),
    val subtitle: List<Track> = emptyList(),
    val video: List<Track> = emptyList(),
    /** `null` when VLC does not tell which track is active. */
    val currentAudio: Int? = null,
    val currentSubtitle: Int? = null,
    val currentVideo: Int? = null,
) {
    fun of(kind: TrackKind): List<Track> = when (kind) {
        TrackKind.AUDIO -> audio
        TrackKind.SUBTITLE -> subtitle
        TrackKind.VIDEO -> video
    }

    fun current(kind: TrackKind): Int? = when (kind) {
        TrackKind.AUDIO -> currentAudio
        TrackKind.SUBTITLE -> currentSubtitle
        TrackKind.VIDEO -> currentVideo
    }

    fun withCurrent(kind: TrackKind, id: Int?): Tracks = when (kind) {
        TrackKind.AUDIO -> copy(currentAudio = id)
        TrackKind.SUBTITLE -> copy(currentSubtitle = id)
        TrackKind.VIDEO -> copy(currentVideo = id)
    }
}

@Serializable
data class Chapters(
    /** Current chapter index or -1. */
    val index: Int = -1,
    val count: Int = 0,
    /** Chapter names when the companion knows them. */
    val names: List<String>? = null,
)

@Serializable
data class Titles(
    val index: Int = -1,
    val count: Int = 0,
)

@Serializable
data class Volume(
    /** VLC volume 0..512, 256 = 100 %. */
    val value: Int = VlcVolume.UNITY,
    val muted: Boolean = false,
) {
    val percent: Int get() = VlcVolume.toPercent(value)
}

@Serializable
data class VideoState(
    val fullscreen: Boolean = false,
    val aspect: String = "default",
)

@Serializable
data class Delays(
    /** Seconds. */
    val audio: Double = 0.0,
    val subtitle: Double = 0.0,
)

@Serializable
data class Flags(
    val random: Boolean = false,
    val loop: Boolean = false,
    val repeat: Boolean = false,
)

/**
 * YouTube in the TV browser (`state.youtube`). [time] is the position when the companion reported it; [updatedAt] is
 * set on arrival, so the position can be moved on while playing.
 */
@Serializable
data class YouTubeState(
    val open: Boolean = false,
    val playing: Boolean = false,
    /** The video last opened from a phone; its thumbnail is at [thumbnailUrl]. */
    val videoId: String? = null,
    val title: String? = null,
    val channel: String? = null,
    val time: Double? = null,
    val length: Double? = null,
    val canNext: Boolean = false,
    val canPrevious: Boolean = false,
    /** An ad plays (the companion's TV extension tells); [time] and [length] are the ad's then. */
    val ad: Boolean = false,
    /** The ad can be skipped now ([app.sofabird.protocol.backend.YouTubeAction.SKIP_AD]). */
    val adSkippable: Boolean = false,
    /** Ads are blocked with uBlock Origin in the TV browser ([app.sofabird.protocol.backend.SystemCommand.YouTubeAdBlock]). */
    val adBlock: Boolean = false,
    /** Sponsor segments are skipped with SponsorBlock ([app.sofabird.protocol.backend.SystemCommand.YouTubeSponsorBlock]). */
    val sponsorBlock: Boolean = false,
    @kotlinx.serialization.Transient val updatedAt: Long = 0,
) {
    val thumbnailUrl: String? get() = videoId?.let { "https://i.ytimg.com/vi/$it/hqdefault.jpg" }

    /** The position now: [time] moved on since [updatedAt] while playing, never past [length]. */
    fun interpolatedTime(nowMs: Long): Double {
        val base = time ?: return 0.0
        if (!playing || updatedAt <= 0L) return base
        val moved = base + (nowMs - updatedAt).coerceAtLeast(0) / 1000.0
        return length?.let { moved.coerceAtMost(it) } ?: moved
    }
}

@Serializable
data class SystemState(
    val volume: Int? = null,
    val muted: Boolean? = null,
    val audioDevice: String? = null,
    /** Index of the monitor with the VLC window (old and new companions). */
    val display: Int? = null,
    /** Stable id of the monitor with the VLC window; `null` = no window (or an old companion). */
    val displayId: String? = null,
    /** Monitor VLC opens on; `null` = not managed (or an old companion). */
    val preferredDisplayId: String? = null,
    val keepAwake: Boolean? = null,
    /** The computer Moonlight streams from; `null` = none paired (or Moonlight is not installed). */
    val moonlightHost: String? = null,
    /** A Moonlight stream is on the TV; `null` = Moonlight is not installed (or an old companion). */
    val moonlightStreaming: Boolean? = null,
)

/** Player model. Mirrors the `state` message of the companion protocol. */
@Serializable
data class PlayerState(
    val vlc: VlcInfo = VlcInfo(),
    val playback: Playback = Playback(),
    val item: PlayingItem = PlayingItem(),
    val tracks: Tracks = Tracks(),
    val chapters: Chapters = Chapters(),
    val titles: Titles = Titles(),
    val volume: Volume = Volume(),
    val video: VideoState = VideoState(),
    val delays: Delays = Delays(),
    val flags: Flags = Flags(),
    val system: SystemState? = null,
    /** Companion `history`: VLC waits paused for "continue / from the beginning". */
    val resume: ResumePrompt? = null,
    /** Companion `history`: the next episode offered after the current one ended. */
    val next: NextEpisode? = null,
    /** Companion `history`: revision counter of the watch history. */
    val history: HistoryInfo? = null,
    /** Companion `library`: revision counter of the library, and whether it is being scanned. */
    val library: LibraryInfo? = null,
    /** Companion `youtube`: YouTube in the TV browser; absent without the capability. */
    val youtube: YouTubeState? = null,
) {
    val isPlaying: Boolean get() = playback.state == PlaybackStatus.PLAYING
    val isStopped: Boolean get() = playback.state == PlaybackStatus.STOPPED

    /**
     * Position interpolated to [nowMs] using monotonic-ish wall clock: while playing, `time`
     * advances with `rate` since `updatedAt`. Never exceeds `length` when it is known.
     */
    fun interpolatedTime(nowMs: Long): Double {
        if (!isPlaying || playback.updatedAt <= 0L) return playback.time.toDouble()
        val elapsed = (nowMs - playback.updatedAt).coerceAtLeast(0) / 1000.0
        val t = playback.time + elapsed * playback.rate
        return if (playback.length > 0) t.coerceIn(0.0, playback.length.toDouble()) else t
    }

    companion object {
        val EMPTY = PlayerState()
    }
}
