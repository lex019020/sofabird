package app.sofabird.data.companion

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import app.sofabird.protocol.companion.BtAddress

/**
 * What the app may do with Bluetooth right now. Only bonded devices are used: no discovery, so no
 * `BLUETOOTH_SCAN` and no location permission; Android 12+ needs `BLUETOOTH_CONNECT` only.
 */
class BluetoothSupport(context: Context) {
    private val appContext = context.applicationContext

    val adapter: BluetoothAdapter?
        get() = runCatching { appContext.getSystemService(BluetoothManager::class.java)?.adapter }.getOrNull()

    val isSupported: Boolean
        get() = appContext.packageManager.hasSystemFeature(PackageManager.FEATURE_BLUETOOTH) && adapter != null

    fun hasConnectPermission(): Boolean = hasConnectPermission(appContext)

    fun isEnabled(): Boolean = runCatching { adapter?.isEnabled == true }.getOrDefault(false)

    /** Bonded devices, or empty without the permission / with the radio off. */
    @SuppressLint("MissingPermission")
    fun bondedDevices(): List<BondedDevice> {
        if (!hasConnectPermission()) return emptyList()
        val devices: Set<BluetoothDevice> = runCatching { adapter?.bondedDevices }.getOrNull().orEmpty()
        return devices.mapNotNull { d ->
            runCatching {
                BondedDevice(
                    name = d.name.orEmpty(),
                    address = BtAddress.normalize(d.address) ?: return@runCatching null,
                    isComputer = d.bluetoothClass?.majorDeviceClass == BluetoothClass.Device.Major.COMPUTER,
                )
            }.getOrNull()
        }
    }

    fun isBonded(address: String): Boolean = bondedDevices().any { BtAddress.same(it.address, address) }

    /** Readiness to reach the PC at [address] (`null` address → [BluetoothStatus.NO_ADDRESS]). */
    fun status(address: String?): BluetoothStatus = BluetoothStatus.of(
        address = address,
        pcAddressRequired = true,
        supported = isSupported,
        permission = hasConnectPermission(),
        enabled = isEnabled(),
        bonded = ::isBonded,
    )

    /** Phone-wide readiness (the "Connect via Bluetooth" picker). */
    fun radioStatus(): BluetoothStatus = BluetoothStatus.of(
        address = null,
        pcAddressRequired = false,
        supported = isSupported,
        permission = hasConnectPermission(),
        enabled = isEnabled(),
        bonded = { true },
    )

    fun remoteDevice(address: String): BluetoothDevice? =
        runCatching { adapter?.getRemoteDevice(BtAddress.normalize(address) ?: address) }.getOrNull()

    /**
     * `cancelDiscovery()` needs `BLUETOOTH_SCAN` on Android 12+, which we do not request; there it is
     * skipped (we never start discovery, other apps rarely leave it running).
     */
    val mayCancelDiscovery: Boolean get() = Build.VERSION.SDK_INT < Build.VERSION_CODES.S

    companion object {
        /** Android 12+ runtime permission for bonded devices and RFCOMM; granted at install below. */
        val CONNECT_PERMISSION: String? =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) Manifest.permission.BLUETOOTH_CONNECT else null

        fun hasConnectPermission(context: Context): Boolean {
            val permission = CONNECT_PERMISSION ?: return true
            return ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED
        }
    }
}
