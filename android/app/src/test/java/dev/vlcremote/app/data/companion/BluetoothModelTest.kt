package dev.vlcremote.app.data.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BluetoothModelTest {

    private val pc = "AA:BB:CC:DD:EE:FF"

    private fun status(
        address: String? = pc,
        supported: Boolean = true,
        permission: Boolean = true,
        enabled: Boolean = true,
        bonded: Set<String> = setOf(pc),
    ) = BluetoothStatus.of(address, pcAddressRequired = true, supported, permission, enabled) { it in bonded }

    @Test
    fun `ready only when every condition holds`() {
        assertEquals(BluetoothStatus.READY, status())
        assertEquals(BluetoothStatus.READY, status(address = "aa:bb:cc:dd:ee:ff"))
        assertEquals(BluetoothStatus.NO_ADDRESS, status(address = null))
        assertEquals(BluetoothStatus.NO_ADDRESS, status(address = "garbage"))
        assertEquals(BluetoothStatus.UNSUPPORTED, status(supported = false))
        assertEquals(BluetoothStatus.NOT_BONDED, status(bonded = emptySet()))
    }

    @Test
    fun `permission is checked before the radio and the bond list`() {
        // Without BLUETOOTH_CONNECT Android 12+ reports no bonded devices: that must not read as "not bonded".
        assertEquals(BluetoothStatus.NO_PERMISSION, status(permission = false, enabled = false, bonded = emptySet()))
        // A switched-off radio lists no bonded devices either.
        assertEquals(BluetoothStatus.OFF, status(enabled = false, bonded = emptySet()))
        assertTrue(BluetoothStatus.NO_PERMISSION.userCanFix)
        assertTrue(BluetoothStatus.OFF.userCanFix)
        assertFalse(BluetoothStatus.NOT_BONDED.userCanFix)
    }

    @Test
    fun `picker status does not need a PC address`() {
        assertEquals(
            BluetoothStatus.READY,
            BluetoothStatus.of(null, pcAddressRequired = false, supported = true, permission = true, enabled = true) { false },
        )
    }

    @Test
    fun `computers are listed first, then by name`() {
        val list = listOf(
            BondedDevice("Galaxy Buds", "00:00:00:00:00:01", isComputer = false),
            BondedDevice("zbook", "00:00:00:00:00:02", isComputer = true),
            BondedDevice("", "00:00:00:00:00:03", isComputer = false),
            BondedDevice("LAPTOP-TV", "00:00:00:00:00:04", isComputer = true),
        )
        assertEquals(
            listOf("LAPTOP-TV", "zbook", "00:00:00:00:00:03", "Galaxy Buds"),
            list.sortedForPicker().map { it.displayName },
        )
    }
}
