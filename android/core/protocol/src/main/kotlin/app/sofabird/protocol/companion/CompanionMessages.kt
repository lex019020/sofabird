package app.sofabird.protocol.companion

import app.sofabird.protocol.model.Chapters
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.Delays
import app.sofabird.protocol.model.Entry
import app.sofabird.protocol.model.Flags
import app.sofabird.protocol.model.HistoryInfo
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.protocol.model.LibraryEpisode
import app.sofabird.protocol.model.LibraryInfo
import app.sofabird.protocol.model.LibraryMovie
import app.sofabird.protocol.model.LibraryShow
import app.sofabird.protocol.model.NextEpisode
import app.sofabird.protocol.model.Playback
import app.sofabird.protocol.model.PlayerState
import app.sofabird.protocol.model.PlayingItem
import app.sofabird.protocol.model.PlaylistItem
import app.sofabird.protocol.model.ResumePrompt
import app.sofabird.protocol.model.ShowDetails
import app.sofabird.protocol.model.SystemState
import app.sofabird.protocol.model.Titles
import app.sofabird.protocol.model.Tracks
import app.sofabird.protocol.model.VideoState
import app.sofabird.protocol.model.VlcInfo
import app.sofabird.protocol.model.Volume
import app.sofabird.protocol.model.YouTubeState
import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonClassDiscriminator
import kotlinx.serialization.json.JsonObject

const val PROTOCOL_VERSION = 1
const val COMPANION_WS_PORT = 47555
const val COMPANION_BEACON_PORT = 47556
const val COMPANION_SERVICE_TYPE = "_sofabird._tcp"
const val BEACON_REQUEST = "SOFABIRD_DISCOVER 1"

/**
 * All messages of the phone <-> companion protocol (protocol/README.md). Serialized with the
 * class discriminator `t`. Every request carries an optional `id` that is echoed in the answer.
 */
@Serializable
@JsonClassDiscriminator("t")
sealed class CompanionMessage {
    abstract val id: String?

    // ------------------------------------------------------------ phone -> PC

    @Serializable
    @SerialName("hello")
    data class Hello(
        val v: Int = PROTOCOL_VERSION,
        val device: DeviceInfo,
        val token: String? = null,
        val pairCode: String? = null,
        override val id: String? = null,
    ) : CompanionMessage()

    @Serializable
    @SerialName("cmd")
    data class Cmd(
        override val id: String,
        val name: String,
        val args: JsonObject = JsonObject(emptyMap()),
    ) : CompanionMessage()

    @Serializable
    @SerialName("sys")
    data class Sys(
        override val id: String,
        val name: String,
        val args: JsonObject = JsonObject(emptyMap()),
    ) : CompanionMessage()

    @Serializable
    @SerialName("browse")
    data class Browse(override val id: String, val uri: String) : CompanionMessage()

    @Serializable
    @SerialName("getState")
    data class GetState(override val id: String) : CompanionMessage()

    @Serializable
    @SerialName("getPlaylist")
    data class GetPlaylist(override val id: String) : CompanionMessage()

    @Serializable
    @SerialName("getTracks")
    data class GetTracks(override val id: String) : CompanionMessage()

    @Serializable
    @SerialName("getAudioDevices")
    data class GetAudioDevices(override val id: String) : CompanionMessage()

    /** `history` capability; [filter] is a [app.sofabird.protocol.model.HistoryFilter] wire value. */
    @Serializable
    @SerialName("getHistory")
    data class GetHistory(
        override val id: String,
        val filter: String = "all",
        val limit: Int = 50,
        val offset: Int = 0,
    ) : CompanionMessage()

    /** Mark watched / unwatched (`finished`) or delete (`remove: true`) one entry; absent fields are not sent. */
    @Serializable
    @SerialName("historyUpdate")
    data class HistoryUpdate(
        override val id: String,
        val uri: String,
        @EncodeDefault(EncodeDefault.Mode.NEVER) val finished: Boolean? = null,
        @EncodeDefault(EncodeDefault.Mode.NEVER) val remove: Boolean? = null,
    ) : CompanionMessage()

    @Serializable
    @SerialName("historyClear")
    data class HistoryClear(override val id: String) : CompanionMessage()

    /** `library` capability: the movies and series. */
    @Serializable
    @SerialName("getLibrary")
    data class GetLibrary(override val id: String) : CompanionMessage()

    /** `library` capability: one series with its episodes ([LibraryShow.key]). */
    @Serializable
    @SerialName("getShow")
    data class GetShow(override val id: String, val key: String) : CompanionMessage()

    @Serializable
    @SerialName("ping")
    data class Ping(override val id: String? = null) : CompanionMessage()

    // ------------------------------------------------------------ PC -> phone

    @Serializable
    @SerialName("welcome")
    data class Welcome(
        val v: Int = PROTOCOL_VERSION,
        val name: String,
        @SerialName("id") val pcId: String? = null,
        val caps: List<String> = emptyList(),
        val token: String? = null,
        val vlc: VlcInfo = VlcInfo(),
        /** PC Bluetooth adapter address (`AA:BB:CC:DD:EE:FF`); absent without an adapter or with BT off. */
        val bt: String? = null,
        /**
         * MACs of the connected adapters that will wake the PC from sleep (wired first): the phone wakes it with them
         * ([MagicPacket]). Empty — it cannot be woken; `null` — not said (an older companion, or not known yet).
         */
        val macs: List<String>? = null,
    ) : CompanionMessage() {
        /** `welcome` uses `id` for the PC GUID, not for request correlation. */
        override val id: String? get() = null
    }

    @Serializable
    @SerialName("ok")
    data class Ok(override val id: String? = null) : CompanionMessage()

    @Serializable
    @SerialName("err")
    data class Err(
        override val id: String? = null,
        val code: String,
        val msg: String? = null,
    ) : CompanionMessage() {
        val errorCode: ErrorCode get() = ErrorCode.fromWire(code)
    }

    /** Full player state (never a diff). Same shape as [PlayerState] plus `t`. */
    @Serializable
    @SerialName("state")
    data class State(
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
        /** `history`: pending resume prompt; absent = none. */
        val resume: ResumePrompt? = null,
        /** `history`: next-episode offer; absent = none. */
        val next: NextEpisode? = null,
        /** `history`: history revision. */
        val history: HistoryInfo? = null,
        /** `library`: library revision. */
        val library: LibraryInfo? = null,
        /** `youtube`: YouTube in the TV browser. */
        val youtube: YouTubeState? = null,
        override val id: String? = null,
    ) : CompanionMessage() {
        fun toPlayerState(): PlayerState = PlayerState(
            vlc = vlc, playback = playback, item = item, tracks = tracks, chapters = chapters,
            titles = titles, volume = volume, video = video, delays = delays, flags = flags, system = system,
            resume = resume, next = next, history = history, library = library, youtube = youtube,
        )

        companion object {
            fun from(state: PlayerState): State = State(
                vlc = state.vlc, playback = state.playback, item = state.item, tracks = state.tracks,
                chapters = state.chapters, titles = state.titles, volume = state.volume,
                video = state.video, delays = state.delays, flags = state.flags, system = state.system,
                resume = state.resume, next = state.next, history = state.history, library = state.library,
                youtube = state.youtube,
            )
        }
    }

    @Serializable
    @SerialName("event")
    data class Event(
        val name: String,
        override val id: String? = null,
    ) : CompanionMessage()

    @Serializable
    @SerialName("entries")
    data class Entries(
        override val id: String? = null,
        val uri: String,
        val items: List<Entry> = emptyList(),
    ) : CompanionMessage()

    @Serializable
    @SerialName("playlist")
    data class Playlist(
        override val id: String? = null,
        val items: List<PlaylistItem> = emptyList(),
    ) : CompanionMessage()

    @Serializable
    @SerialName("tracks")
    data class TracksMsg(
        override val id: String? = null,
        val audio: List<app.sofabird.protocol.model.Track> = emptyList(),
        val subtitle: List<app.sofabird.protocol.model.Track> = emptyList(),
        val video: List<app.sofabird.protocol.model.Track> = emptyList(),
        val currentAudio: Int? = null,
        val currentSubtitle: Int? = null,
        val currentVideo: Int? = null,
    ) : CompanionMessage() {
        fun toTracks() = Tracks(audio, subtitle, video, currentAudio, currentSubtitle, currentVideo)
    }

    @Serializable
    @SerialName("audioDevices")
    data class AudioDevices(
        override val id: String? = null,
        val items: List<AudioDevice> = emptyList(),
    ) : CompanionMessage()

    @Serializable
    @SerialName("displays")
    data class Displays(
        override val id: String? = null,
        val items: List<Display> = emptyList(),
        /** Monitor VLC opens on; `null` = not managed (or an old companion). */
        val preferredId: String? = null,
    ) : CompanionMessage() {
        fun toList() = DisplayList(items, preferredId)
    }

    /** Answer to [GetHistory]. */
    @Serializable
    @SerialName("history")
    data class History(
        override val id: String? = null,
        val total: Int = 0,
        val items: List<HistoryItem> = emptyList(),
    ) : CompanionMessage()

    /** Answer to [GetLibrary]. */
    @Serializable
    @SerialName("library")
    data class Library(
        override val id: String? = null,
        val revision: Long = 0,
        val scanning: Boolean = false,
        val movies: List<LibraryMovie> = emptyList(),
        val shows: List<LibraryShow> = emptyList(),
        val missingFolders: List<String> = emptyList(),
    ) : CompanionMessage() {
        fun toLibrary() = app.sofabird.protocol.model.Library(revision, scanning, movies, shows, missingFolders)
    }

    /** Answer to [GetShow]. */
    @Serializable
    @SerialName("show")
    data class Show(
        override val id: String? = null,
        val show: LibraryShow,
        val episodes: List<LibraryEpisode> = emptyList(),
    ) : CompanionMessage() {
        fun toDetails() = ShowDetails(show, episodes)
    }

    @Serializable
    @SerialName("pong")
    data class Pong(override val id: String? = null) : CompanionMessage()

    /** UDP discovery answer (same `t`-tagged JSON, sent over UDP unicast). */
    @Serializable
    @SerialName("beacon")
    data class Beacon(
        val name: String,
        @SerialName("id") val pcId: String? = null,
        val wsPort: Int = COMPANION_WS_PORT,
        val protocol: Int = PROTOCOL_VERSION,
        val needsPairing: Boolean = false,
        /** PC Bluetooth adapter address, see [Welcome.bt]. */
        val bt: String? = null,
        /** See [Welcome.macs]. */
        val macs: List<String>? = null,
    ) : CompanionMessage() {
        override val id: String? get() = null
    }
}

@Serializable
data class DeviceInfo(
    val name: String,
    val id: String,
    /** Phone UI language (BCP 47, e.g. "ru-RU"); the companion renders err.msg in it. Optional. */
    val locale: String? = null,
)

@Serializable
data class AudioDevice(val id: String, val name: String, val current: Boolean = false)

/**
 * One monitor from `sys listDisplays`. Everything except [index] is optional: old companions send only
 * `index`/`name`/`primary`/`bounds`.
 */
@Serializable
data class Display(
    /** Windows enumeration order; changes when a monitor is switched off. */
    val index: Int,
    /** Stable monitor id (survives the TV being switched off and on). */
    val id: String? = null,
    /** Friendly name from EDID ("LG TV SSCR2"). */
    val name: String? = null,
    /** [KIND_INTERNAL] (laptop panel) or [KIND_EXTERNAL] (HDMI/DP/…); unknown values are kept as-is. */
    val kind: String? = null,
    val primary: Boolean = false,
    /** The VLC window is on this monitor now. */
    val current: Boolean? = null,
    /** VLC opens on this monitor. */
    val preferred: Boolean? = null,
    val bounds: Bounds? = null,
) {
    val isInternal: Boolean get() = kind == KIND_INTERNAL
    val isExternal: Boolean get() = kind == KIND_EXTERNAL

    companion object {
        const val KIND_INTERNAL = "internal"
        const val KIND_EXTERNAL = "external"
    }
}

/** `listDisplays` result. */
data class DisplayList(
    val items: List<Display> = emptyList(),
    val preferredId: String? = null,
) {
    /**
     * The companion identifies monitors by stable ids: it remembers a preferred monitor and accepts
     * `moveToDisplay {displayId}` / `preferredDisplay`. Old companions: index-only moves.
     */
    val supportsIds: Boolean get() = items.isNotEmpty() && items.all { !it.id.isNullOrEmpty() }
}

@Serializable
data class Bounds(val x: Int = 0, val y: Int = 0, val width: Int = 0, val height: Int = 0)

/** `GET /info` on the companion port (no auth; discovery only). */
@Serializable
data class CompanionInfo(
    val name: String,
    val version: String? = null,
    val protocol: Int = PROTOCOL_VERSION,
    val vlc: VlcInfo? = null,
    val wsPort: Int = COMPANION_WS_PORT,
    val needsPairing: Boolean = false,
    /** PC Bluetooth adapter address, see [CompanionMessage.Welcome.bt]. */
    val bt: String? = null,
    /** See [CompanionMessage.Welcome.macs]. */
    val macs: List<String>? = null,
)

enum class ErrorCode(val wire: String) {
    /** Handshake: the phone is not paired and sent no code; the companion shows a code on the PC. */
    PAIRING_REQUIRED("pairing_required"),

    /** Handshake: wrong/expired code or too many attempts (old companions: also "not paired"). */
    UNAUTHORIZED("unauthorized"),
    BAD_REQUEST("bad_request"),
    VLC_NOT_RUNNING("vlc_not_running"),
    VLC_ERROR("vlc_error"),
    UNSUPPORTED("unsupported"),
    INTERNAL("internal"),
    UNKNOWN("");

    companion object {
        fun fromWire(code: String): ErrorCode = entries.firstOrNull { it != UNKNOWN && it.wire == code } ?: UNKNOWN
    }
}

/**
 * Maps an `err` answer to `hello` to a connection error. [sentPairCode] tells whether the `hello`
 * carried a pairing code: then `unauthorized` means "wrong or expired code"; without a code it is
 * an old companion's way of saying "pairing required" (protocol/README.md, compatibility note).
 */
fun CompanionMessage.Err.toHandshakeError(sentPairCode: Boolean): ConnectionError = when (errorCode) {
    ErrorCode.PAIRING_REQUIRED -> ConnectionError.PairingRequired
    ErrorCode.UNAUTHORIZED -> if (sentPairCode) ConnectionError.InvalidPairCode else ConnectionError.PairingRequired
    else -> ConnectionError.Other(msg ?: code)
}

/** Companion returned `err`. */
class CompanionErrorException(val code: ErrorCode, val rawCode: String, message: String?) :
    Exception(message ?: rawCode)
