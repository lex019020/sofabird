package dev.vlcremote.protocol.model

/** VLC's volume scale (HTTP `volume`, protocol `volume.value`): 0..512, where 256 is 100 %. */
object VlcVolume {
    /** 100 %: VLC plays at the source level. */
    const val UNITY = 256

    /** 200 %: the highest value VLC accepts. */
    const val MAX = 512

    fun clamp(value: Int): Int = value.coerceIn(0, MAX)

    /** A 0..100 % value (system volume, the media notification) on VLC's scale. */
    fun fromPercent(percent: Int): Int = percent.coerceIn(0, 100) * UNITY / 100

    /** Rounded percentage of a VLC volume value. */
    fun toPercent(value: Int): Int = (value * 100 + UNITY / 2) / UNITY
}
