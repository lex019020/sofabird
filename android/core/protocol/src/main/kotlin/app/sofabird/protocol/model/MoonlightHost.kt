package app.sofabird.protocol.model

import kotlinx.serialization.Serializable

/**
 * A computer paired in Moonlight on the TV PC (`sys moonlightHosts`, capability `moonlight`) and the apps it offers,
 * "Desktop" first.
 */
@Serializable
data class MoonlightHost(
    val name: String,
    /** Empty — Moonlight on the PC does not know them yet (never opened): "Desktop" can be started all the same. */
    val apps: List<String> = emptyList(),
    /** A refresh was asked but the computer did not answer (off, asleep): [apps] is what Moonlight remembers. */
    val stale: Boolean = false,
) {
    /** What can be started: its apps, or "Desktop" alone while they are unknown. */
    val choices: List<String> get() = apps.ifEmpty { listOf(DESKTOP) }

    companion object {
        /** Sunshine's app that shows the computer's whole screen. */
        const val DESKTOP = "Desktop"
    }
}
