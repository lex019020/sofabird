package app.sofabird.data.servers

import app.sofabird.data.discovery.DiscoveryRepository
import app.sofabird.domain.model.CompanionSighting
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onEach

/**
 * Keeps saved companions in step with what discovery sees: a PC paired over Bluetooth gets its LAN
 * address as soon as its beacon answers (matched by installation GUID), a PC paired over Wi-Fi
 * learns its Bluetooth address, a PC that DHCP moved gets the new IP.
 */
class CompanionServerSync(
    private val discovery: DiscoveryRepository,
    private val servers: ServerRepository,
    private val scope: CoroutineScope,
) {
    private var started = false

    fun start() {
        if (started) return
        started = true
        discovery.hosts
            .map { hosts ->
                hosts.values
                    .filter { it.pcId != null || it.bt != null }
                    .map { CompanionSighting(pcId = it.pcId, host = it.host, wsPort = it.wsPort, btAddress = it.bt) }
                    .toSet()
            }
            .distinctUntilChanged()
            .onEach { sightings -> sightings.forEach { servers.applySighting(it) } }
            .launchIn(scope)
    }
}
