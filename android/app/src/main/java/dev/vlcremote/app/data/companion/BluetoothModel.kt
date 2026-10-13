package dev.vlcremote.app.data.companion

import dev.vlcremote.protocol.companion.BtAddress

/** Can the phone use Bluetooth to reach a PC right now. */
enum class BluetoothStatus {
    READY,

    /** The PC address is not known (never seen in `welcome`/beacon/`/info`). */
    NO_ADDRESS,

    /** No Bluetooth on this phone. */
    UNSUPPORTED,

    /** Android 12+: `BLUETOOTH_CONNECT` ("Nearby devices") not granted. */
    NO_PERMISSION,

    /** The radio is off. */
    OFF,

    /** The PC is not among the phone's bonded devices. */
    NOT_BONDED;

    /** The user can fix it from the app (grant the permission / turn Bluetooth on). */
    val userCanFix: Boolean get() = this == NO_PERMISSION || this == OFF

    companion object {
        /**
         * Order of the checks matters: without the permission Android 12+ does not even tell
         * which devices are bonded, and a switched-off radio reports no bonded devices at all.
         * [address] `null` = phone-wide status (the device picker).
         */
        fun of(
            address: String?,
            pcAddressRequired: Boolean,
            supported: Boolean,
            permission: Boolean,
            enabled: Boolean,
            bonded: (String) -> Boolean,
        ): BluetoothStatus {
            val normalized = BtAddress.normalize(address)
            if (pcAddressRequired && normalized == null) return NO_ADDRESS
            if (!supported) return UNSUPPORTED
            if (!permission) return NO_PERMISSION
            if (!enabled) return OFF
            if (normalized != null && !bonded(normalized)) return NOT_BONDED
            return READY
        }
    }
}

/** A device from the phone's bonded list, as the "Connect via Bluetooth" picker shows it. */
data class BondedDevice(
    val name: String,
    val address: String,
    /** `BluetoothClass.Device.Major.COMPUTER`: listed first. */
    val isComputer: Boolean,
) {
    val displayName: String get() = name.ifBlank { address }
}

/** Computers first, then by name; the PC with the companion is almost always on top. */
fun List<BondedDevice>.sortedForPicker(): List<BondedDevice> =
    sortedWith(compareByDescending<BondedDevice> { it.isComputer }.thenBy { it.displayName.lowercase() })
