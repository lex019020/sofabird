package dev.vlcremote.protocol.vlc

import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.TrackKind
import dev.vlcremote.protocol.model.VlcVolume
import java.net.URLEncoder

/**
 * Builds query strings for `status.json?command=…`. Values are percent-encoded; VLC does not
 * decode `+` as space, and a literal `+` (relative seek/volume) must be sent as `%2B`.
 */
@Suppress("TooManyFunctions") // one function per VLC HTTP command
object VlcCommands {
    const val STATUS_PATH = "/requests/status.json"
    const val PLAYLIST_PATH = "/requests/playlist.json"
    const val BROWSE_PATH = "/requests/browse.json"
    const val TRACKS_PATH = "/requests/tracks.json"
    const val SETVAR_PATH = "/requests/setvar.json"
    const val ART_PATH = "/art"

    /** Query string without the leading `?`. */
    fun query(command: String, vararg params: Pair<String, String?>): String = buildString {
        append("command=").append(command)
        for ((k, v) in params) {
            if (v == null) continue
            append('&').append(k).append('=').append(encode(v))
        }
    }

    fun encode(value: String): String =
        URLEncoder.encode(value, Charsets.UTF_8.name())
            .replace("+", "%20")
            .replace("*", "%2A")
            .replace("%7E", "~")

    fun signed(delta: Long): String = if (delta >= 0) "+$delta" else delta.toString()
    fun signed(delta: Int): String = if (delta >= 0) "+$delta" else delta.toString()

    // Playlist / transport
    fun play(plId: Int? = null) = query("pl_play", "id" to plId?.toString())
    fun togglePause(plId: Int? = null) = query("pl_pause", "id" to plId?.toString())
    fun forcePause() = query("pl_forcepause")
    fun forceResume() = query("pl_forceresume")
    fun stop() = query("pl_stop")
    fun next() = query("pl_next")
    fun previous() = query("pl_previous")
    fun delete(plId: Int) = query("pl_delete", "id" to plId.toString())
    fun empty() = query("pl_empty")
    fun toggleRandom() = query("pl_random")
    fun toggleLoop() = query("pl_loop")
    fun toggleRepeat() = query("pl_repeat")
    fun sort(id: Int, mode: Int) = query("pl_sort", "id" to id.toString(), "val" to mode.toString())

    // Playback
    fun toggleFullscreen() = query("fullscreen")
    fun volume(value: Int) = query("volume", "val" to VlcVolume.clamp(value).toString())
    fun volumeBy(delta: Int) = query("volume", "val" to signed(delta))
    fun seekTo(seconds: Long) = query("seek", "val" to seconds.coerceAtLeast(0).toString())
    fun seekBy(delta: Long) = query("seek", "val" to signed(delta))
    fun seekPercent(percent: Int) = query("seek", "val" to "${percent.coerceIn(0, 100)}%")
    fun rate(value: Double) = query("rate", "val" to formatNumber(value))
    fun audioDelay(seconds: Double) = query("audiodelay", "val" to formatNumber(seconds))
    fun subtitleDelay(seconds: Double) = query("subdelay", "val" to formatNumber(seconds))
    fun aspectRatio(value: String) = query("aspectratio", "val" to value)
    fun title(index: Int) = query("title", "val" to index.toString())
    fun chapter(index: Int) = query("chapter", "val" to index.toString())
    fun audioTrack(id: Int) = query("audio_track", "val" to id.toString())
    fun videoTrack(id: Int) = query("video_track", "val" to id.toString())
    fun subtitleTrack(id: Int) = query("subtitle_track", "val" to id.toString())

    fun track(kind: TrackKind, id: Int) = when (kind) {
        TrackKind.AUDIO -> audioTrack(id)
        TrackKind.SUBTITLE -> subtitleTrack(id)
        TrackKind.VIDEO -> videoTrack(id)
    }

    // Input
    fun inPlay(uri: String, vararg options: String) = query(
        "in_play",
        "input" to uri,
        *options.map { "option" to it }.toTypedArray(),
    )

    fun inEnqueue(uri: String) = query("in_enqueue", "input" to uri)
    fun addSubtitle(uri: String) = query("addsubtitle", "val" to uri)
    fun key(hotkey: String) = query("key", "val" to hotkey.removePrefix("key-"))
    fun snapshot() = query("snapshot")

    /** Closes VLC — anybody with the HTTP password can do this. */
    fun quit() = inPlay("vlc://quit")

    /** `browse.json` query. */
    fun browse(uri: String): String = "uri=${encode(uri)}"

    /** `setvar.json` (level 1). */
    fun setVar(obj: String, name: String, value: String): String =
        "obj=${encode(obj)}&name=${encode(name)}&val=${encode(value)}"

    /**
     * Maps a [PlayerCommand] to the sequence of query strings needed in direct mode. Toggle-only
     * commands (random/loop/repeat/fullscreen) need the current value to decide whether to send.
     * Returns an empty list when nothing has to be sent (already in the requested state).
     */
    fun forCommand(command: PlayerCommand, current: CurrentToggles): List<String> = when (command) {
        is PlayerCommand.Play -> listOf(if (command.plId != null) play(command.plId) else forceResume())
        PlayerCommand.Pause -> listOf(forcePause())
        PlayerCommand.TogglePause -> listOf(togglePause())
        PlayerCommand.Stop -> listOf(stop())
        PlayerCommand.Next -> listOf(next())
        PlayerCommand.Prev -> listOf(previous())
        is PlayerCommand.SeekTo -> listOf(seekTo(command.seconds))
        is PlayerCommand.SeekBy -> listOf(seekBy(command.delta))
        is PlayerCommand.SetVolume -> listOf(volume(command.value))
        is PlayerCommand.VolumeBy -> listOf(volumeBy(command.delta))
        is PlayerCommand.Mute -> emptyList() // handled by the backend (remembers the level)
        is PlayerCommand.Rate -> listOf(rate(command.value))
        is PlayerCommand.SelectTrack -> listOf(track(command.kind, command.id))
        is PlayerCommand.Chapter -> listOf(chapter(command.index))
        is PlayerCommand.ChapterBy -> listOf(chapter((current.chapter + command.delta).coerceAtLeast(0)))
        is PlayerCommand.Title -> listOf(title(command.index))
        is PlayerCommand.Delay -> listOf(
            if (command.kind == TrackKind.AUDIO) audioDelay(command.seconds) else subtitleDelay(command.seconds),
        )
        is PlayerCommand.Fullscreen -> when (command.on) {
            null -> listOf(toggleFullscreen())
            current.fullscreen -> emptyList()
            else -> listOf(toggleFullscreen())
        }
        is PlayerCommand.Aspect -> listOf(aspectRatio(command.value))
        is PlayerCommand.Open -> buildList {
            if (command.enqueue) {
                add(inEnqueue(command.uri))
            } else {
                val options = buildList {
                    command.subtitle?.let { add("sub-file=$it") }
                    command.audioTrack?.let { add("audio-track-id=$it") }
                    command.subtitleTrack?.let { add("sub-track-id=$it") }
                }
                add(inPlay(command.uri, *options.toTypedArray()))
            }
        }
        is PlayerCommand.PlaylistRemove -> listOf(delete(command.plId))
        PlayerCommand.PlaylistClear -> listOf(empty())
        is PlayerCommand.Random -> if (command.on == current.random) emptyList() else listOf(toggleRandom())
        is PlayerCommand.Loop -> if (command.on == current.loop) emptyList() else listOf(toggleLoop())
        is PlayerCommand.Repeat -> if (command.on == current.repeat) emptyList() else listOf(toggleRepeat())
        is PlayerCommand.Launch -> emptyList() // companion only
        PlayerCommand.Quit -> listOf(quit())
        is PlayerCommand.Resume, PlayerCommand.DismissNext -> emptyList() // companion only (history)
    }

    /** Values of toggle-style flags needed to turn `set` commands into VLC toggles. */
    data class CurrentToggles(
        val fullscreen: Boolean = false,
        val random: Boolean = false,
        val loop: Boolean = false,
        val repeat: Boolean = false,
        val chapter: Int = 0,
    )

    private fun formatNumber(value: Double): String {
        if (value == Math.floor(value) && !value.isInfinite()) return value.toLong().toString()
        return String.format(java.util.Locale.ROOT, "%.3f", value).trimEnd('0').trimEnd('.')
    }
}
