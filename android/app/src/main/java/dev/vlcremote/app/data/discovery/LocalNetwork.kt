package dev.vlcremote.app.data.discovery

import android.content.Context
import android.net.ConnectivityManager
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/** Own IPv4 address and subnet helpers (active network first, interface enumeration as fallback). */
class LocalNetwork(private val context: Context) {

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
        val hostBits = 32 - own.prefixLength.coerceIn(1, 31)
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

    /** All hosts of the /24 containing the own address, nearest to the own address first. */
    fun subnet24Candidates(): List<String> {
        val own = ownAddress() ?: return emptyList()
        val bytes = own.address.address
        val ownLast = bytes[3].toInt() and 0xff
        val prefix = "${bytes[0].toInt() and 0xff}.${bytes[1].toInt() and 0xff}.${bytes[2].toInt() and 0xff}."
        return (1..254)
            .filter { it != ownLast }
            .sortedBy { kotlin.math.abs(it - ownLast) }
            .map { prefix + it }
    }
}
