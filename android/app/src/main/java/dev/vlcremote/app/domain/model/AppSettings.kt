package dev.vlcremote.app.domain.model

enum class ThemeMode { SYSTEM, DARK, LIGHT }

data class AppSettings(
    /** Hardware volume keys change the PC volume while the app is in front. */
    val volumeKeysControlPc: Boolean = true,
    /** Step in VLC units (0..512); 13 ≈ 5 %. */
    val volumeKeyStep: Int = 13,
    /** Short / long seek steps in seconds. */
    val seekShortSec: Int = 10,
    val seekLongSec: Int = 30,
    val theme: ThemeMode = ThemeMode.DARK,
    val trueBlack: Boolean = false,
    val dynamicColor: Boolean = false,
    /** Keep the connection (foreground service + notification) while the app is in the background. */
    val keepConnectionInBackground: Boolean = true,
    val haptics: Boolean = true,
    /** Library: hide non-media files. */
    val libraryMediaOnly: Boolean = true,
) {
    companion object {
        val VOLUME_STEPS = listOf(5, 13, 26) // ≈2 %, 5 %, 10 %
        val SEEK_SHORT_STEPS = listOf(5, 10, 15, 30)
        val SEEK_LONG_STEPS = listOf(30, 60, 90, 300)
    }
}
