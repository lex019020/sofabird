package app.sofabird.data.discovery

import android.content.Context
import android.net.ConnectivityManager
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/** Own IPv4 address and its subnet's broadcast (active network first, interface enumeration as fallback). */
class LocalNetwork(private val context: Context) {

    private companion object {
        const val IPV4_BITS = 32
    }

    data class Address(val address: Inet4Address, val prefixLength: Int)

    fun ownAddress(): Address? {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        val active = cm?.activeNetwork?.let { cm.getLinkProperties(it) }
        active?.linkAddresses?.forEach { la ->
            val a = la.address
            if (a is Inet4Address && !a.isLoopbackAddress) return Address(a, la.prefixLength)
        }
        return runCatching {
            NetworkInterface.getNetworkInterfaces()?.toList()
                ?.filter { it.isUp && !it.isLoopback }
                ?.flatMap { it.interfaceAddresses }
                ?.firstOrNull { it.address is Inet4Address && !it.address.isLoopbackAddress }
                ?.let { Address(it.address as Inet4Address, it.networkPrefixLength.toInt()) }
        }.getOrNull()
    }

    /** Directed broadcast of the own subnet, e.g. 192.168.1.255. */
    fun directedBroadcast(): InetAddress? {
        val own = ownAddress() ?: return null
        val ip = own.address.address.fold(0L) { acc, b -> (acc shl 8) or (b.toLong() and 0xff) }
        val hostBits = IPV4_BITS - own.prefixLength.coerceIn(1, IPV4_BITS - 1)
        val broadcast = ip or ((1L shl hostBits) - 1)
        return InetAddress.getByAddress(
            byteArrayOf(
                (broadcast shr 24 and 0xff).toByte(),
                (broadcast shr 16 and 0xff).toByte(),
                (broadcast shr 8 and 0xff).toByte(),
                (broadcast and 0xff).toByte(),
            ),
        )
    }
}
