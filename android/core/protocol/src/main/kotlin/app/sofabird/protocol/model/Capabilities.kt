package app.sofabird.protocol.model

/** Capability flags as exchanged on the wire (`caps` in `welcome`). */
enum class Capability(val wire: String) {
    CURRENT_TRACK("currentTrack"),
    CHAPTER_NAMES("chapterNames"),
    AUDIO_DEVICES("audioDevices"),
    PUSH_EVENTS("pushEvents"),
    LAUNCH_VLC("launchVlc"),
    WINDOW_CONTROL("windowControl"),
    POWER("power"),
    SYSTEM_VOLUME("systemVolume"),
    METADATA("metadata"),
    BLUETOOTH("bluetooth"),

    /** Watch history, resume prompt and next-episode offer (companion). */
    HISTORY("history"),

    /** Movies and series of the PC's media folders with TMDB metadata (companion). */
    LIBRARY("library"),

    /** Moonlight is installed on the PC: start/stop streaming another computer's screen to the TV. */
    MOONLIGHT("moonlight");

    companion object {
        private val byWire = entries.associateBy { it.wire }
        fun fromWire(value: String): Capability? = byWire[value]
    }
}

/** Typed view over the set of capability strings. UI shows controls only when the flag is set. */
data class Capabilities(val flags: Set<Capability> = emptySet()) {
    val currentTrack: Boolean get() = Capability.CURRENT_TRACK in flags
    val chapterNames: Boolean get() = Capability.CHAPTER_NAMES in flags
    val audioDevices: Boolean get() = Capability.AUDIO_DEVICES in flags
    val pushEvents: Boolean get() = Capability.PUSH_EVENTS in flags
    val launchVlc: Boolean get() = Capability.LAUNCH_VLC in flags
    val windowControl: Boolean get() = Capability.WINDOW_CONTROL in flags
    val power: Boolean get() = Capability.POWER in flags
    val systemVolume: Boolean get() = Capability.SYSTEM_VOLUME in flags
    val metadata: Boolean get() = Capability.METADATA in flags
    val bluetooth: Boolean get() = Capability.BLUETOOTH in flags
    val history: Boolean get() = Capability.HISTORY in flags
    val library: Boolean get() = Capability.LIBRARY in flags
    val moonlight: Boolean get() = Capability.MOONLIGHT in flags

    operator fun contains(capability: Capability): Boolean = capability in flags

    fun toWire(): List<String> = flags.map { it.wire }

    companion object {
        val NONE = Capabilities()

        /** Level 0: plain HTTP interface. */
        val LEVEL0 = Capabilities()

        /** Level 1: HTTP + our Lua pages. */
        val LEVEL1 = Capabilities(setOf(Capability.CURRENT_TRACK, Capability.CHAPTER_NAMES))

        fun fromWire(values: Collection<String>): Capabilities =
            Capabilities(values.mapNotNull { Capability.fromWire(it) }.toSet())

        fun of(vararg flags: Capability) = Capabilities(flags.toSet())
    }
}
