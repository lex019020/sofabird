package dev.vlcremote.protocol.companion

import dev.vlcremote.protocol.model.Chapters
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.Delays
import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.Flags
import dev.vlcremote.protocol.model.Playback
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.PlayingItem
import dev.vlcremote.protocol.model.PlaylistItem
import dev.vlcremote.protocol.model.SystemState
import dev.vlcremote.protocol.model.Titles
import dev.vlcremote.protocol.model.Tracks
import dev.vlcremote.protocol.model.VideoState
import dev.vlcremote.protocol.model.VlcInfo
import dev.vlcremote.protocol.model.Volume
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonClassDiscriminator
import kotlinx.serialization.json.JsonObject

const val PROTOCOL_VERSION = 1
const val COMPANION_WS_PORT = 47555
const val COMPANION_BEACON_PORT = 47556
const val COMPANION_SERVICE_TYPE = "_vlcremote._tcp"
const val BEACON_REQUEST = "VLCREMOTE_DISCOVER 1"

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
        override val id: String? = null,
    ) : CompanionMessage() {
        fun toPlayerState(): PlayerState = PlayerState(
            vlc = vlc, playback = playback, item = item, tracks = tracks, chapters = chapters,
            titles = titles, volume = volume, video = video, delays = delays, flags = flags, system = system,
        )

        companion object {
            fun from(state: PlayerState): State = State(
                vlc = state.vlc, playback = state.playback, item = state.item, tracks = state.tracks,
                chapters = state.chapters, titles = state.titles, volume = state.volume,
                video = state.video, delays = state.delays, flags = state.flags, system = state.system,
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
        val audio: List<dev.vlcremote.protocol.model.Track> = emptyList(),
        val subtitle: List<dev.vlcremote.protocol.model.Track> = emptyList(),
        val video: List<dev.vlcremote.protocol.model.Track> = emptyList(),
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
    ) : CompanionMessage()

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

@Serializable
data class Display(
    val index: Int,
    val name: String? = null,
    val primary: Boolean = false,
    val bounds: Bounds? = null,
)

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
