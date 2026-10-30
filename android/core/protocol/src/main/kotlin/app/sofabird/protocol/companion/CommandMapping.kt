package app.sofabird.protocol.companion

import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.backend.SystemCommand
import app.sofabird.protocol.model.TrackKind
import app.sofabird.protocol.model.VlcVolume
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Turns typed commands into `cmd` / `sys` messages. */
object CommandMapping {

    fun toCmd(id: String, command: PlayerCommand): CompanionMessage.Cmd {
        val (name, args) = when (command) {
            is PlayerCommand.Play -> "play" to args { command.plId?.let { put("plId", it) } }
            PlayerCommand.Pause -> "pause" to EMPTY
            PlayerCommand.TogglePause -> "togglePause" to EMPTY
            PlayerCommand.Stop -> "stop" to EMPTY
            PlayerCommand.Next -> "next" to EMPTY
            PlayerCommand.Prev -> "prev" to EMPTY
            is PlayerCommand.SeekTo -> "seek" to args { put("seconds", command.seconds) }
            is PlayerCommand.SeekBy -> "seek" to args { put("delta", command.delta) }
            is PlayerCommand.SetVolume -> "volume" to args { put("value", VlcVolume.clamp(command.value)) }
            is PlayerCommand.VolumeBy -> "volume" to args { put("delta", command.delta) }
            is PlayerCommand.Mute -> "mute" to args { put("on", command.on) }
            is PlayerCommand.Rate -> "rate" to args { put("value", command.value) }
            is PlayerCommand.SelectTrack -> "track" to args {
                put("kind", command.kind.wire)
                put("id", command.id)
            }
            is PlayerCommand.Chapter -> "chapter" to args { put("index", command.index) }
            is PlayerCommand.ChapterBy -> "chapter" to args { put("delta", command.delta) }
            is PlayerCommand.Title -> "title" to args { put("index", command.index) }
            is PlayerCommand.Delay -> "delay" to args {
                put("kind", if (command.kind == TrackKind.AUDIO) "audio" else "subtitle")
                put("seconds", command.seconds)
            }
            is PlayerCommand.Fullscreen -> "fullscreen" to args { command.on?.let { put("on", it) } }
            is PlayerCommand.Aspect -> "aspect" to args { put("value", command.value) }
            is PlayerCommand.Open -> "open" to args {
                put("uri", command.uri)
                if (command.enqueue) put("enqueue", true)
                command.subtitle?.let { put("subtitle", it) }
                command.audioTrack?.let { put("audioTrack", it) }
                command.subtitleTrack?.let { put("subtitleTrack", it) }
            }
            is PlayerCommand.PlaylistRemove -> "playlistRemove" to args { put("plId", command.plId) }
            PlayerCommand.PlaylistClear -> "playlistClear" to EMPTY
            is PlayerCommand.Random -> "random" to args { put("on", command.on) }
            is PlayerCommand.Loop -> "loop" to args { put("on", command.on) }
            is PlayerCommand.Repeat -> "repeat" to args { put("on", command.on) }
            is PlayerCommand.Launch -> "launch" to args {
                command.uri?.let { put("uri", it) }
                command.display?.let { put("display", it) }
            }
            PlayerCommand.Quit -> "quit" to EMPTY
            is PlayerCommand.Resume -> "resume" to args { put("choice", command.choice.wire) }
            PlayerCommand.DismissNext -> "dismissNext" to EMPTY
        }
        return CompanionMessage.Cmd(id = id, name = name, args = args)
    }

    fun toSys(id: String, command: SystemCommand): CompanionMessage.Sys {
        val (name, args) = when (command) {
            is SystemCommand.MoveToDisplay -> "moveToDisplay" to args {
                // New companions work by stable id; the index is only for those that report no ids.
                if (command.displayId != null) put("displayId", command.displayId) else put("display", command.legacyIndex)
            }
            is SystemCommand.PreferredDisplay -> "preferredDisplay" to args { put("displayId", command.displayId) }
            SystemCommand.ListDisplays -> "listDisplays" to EMPTY
            is SystemCommand.KeepAwake -> "keepAwake" to args { put("on", command.on) }
            is SystemCommand.DisplayPower -> "displayPower" to args { put("on", command.on) }
            SystemCommand.Sleep -> "sleep" to EMPTY
            SystemCommand.Shutdown -> "shutdown" to EMPTY
            is SystemCommand.AudioDevice -> "audioDevice" to args { put("id", command.id) }
            is SystemCommand.SystemVolume -> "systemVolume" to args {
                command.value?.let { put("value", it.coerceIn(0, 100)) }
                command.delta?.let { put("delta", it) }
                command.mute?.let { put("mute", it) }
            }
            is SystemCommand.MoonlightStart -> "moonlightStart" to args {
                command.host?.let { put("host", it) }
                command.app?.let { put("app", it) }
            }
            SystemCommand.MoonlightStop -> "moonlightStop" to EMPTY
            is SystemCommand.YouTubeOpen -> "youtubeOpen" to args { put("url", command.url) }
            is SystemCommand.YouTubeAdBlock -> "youtubeAdBlock" to args { put("on", command.on) }
            is SystemCommand.YouTubeSponsorBlock -> "youtubeSponsorBlock" to args { put("on", command.on) }
            is SystemCommand.YouTube -> "youtube" to args {
                put("action", command.action.wire)
                command.time?.let { put("time", it.coerceAtLeast(0.0)) }
                command.value?.let { put("value", it) }
            }
        }
        return CompanionMessage.Sys(id = id, name = name, args = args)
    }

    private val EMPTY = JsonObject(emptyMap())

    private inline fun args(block: kotlinx.serialization.json.JsonObjectBuilder.() -> Unit): JsonObject =
        buildJsonObject(block)
}
