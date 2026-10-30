package app.sofabird.data.player

import app.sofabird.data.companion.BluetoothStatus
import app.sofabird.data.companion.BluetoothSupport
import app.sofabird.data.companion.isTerminalHandshakeError
import app.sofabird.data.secrets.SecretStore
import app.sofabird.data.servers.ServerRepository
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.domain.model.SavedServer
import app.sofabird.domain.model.findCompanion
import app.sofabird.protocol.companion.BtAddress
import kotlinx.coroutines.flow.first
import java.util.UUID

/**
 * [CompanionLink] over the real pieces: [ConnectionTester] for the one-shot `hello`, tokens in the
 * encrypted [SecretStore] (keyed by the saved server id, which for new companions is the companion's
 * installation GUID), [ServerRepository] and [PlayerController] for the actual connection.
 */
class CompanionPairingLink(
    private val tester: ConnectionTester,
    private val servers: ServerRepository,
    private val secrets: SecretStore,
    private val controller: PlayerController,
    private val identity: DeviceIdentity,
    private val bluetooth: BluetoothSupport,
    private val settings: SettingsRepository,
) : CompanionLink {

    private suspend fun saved(target: PairingTarget, pcId: String? = target.pcId): SavedServer? =
        servers.servers.first().findCompanion(target.host, target.wsPort, pcId, target.serverId, target.btAddress)

    override suspend fun storedToken(target: PairingTarget): String? =
        saved(target)?.let { secrets.companionToken(it.id) }

    override suspend fun hello(target: PairingTarget, token: String?, pairCode: String?): TestResult {
        val bt = target.btAddress
        if (target.viaBluetooth) return helloBluetooth(bt!!, token, pairCode)
        // With the stored token only the pinned certificate will do; a new pairing code is the user trusting the PC
        // (again — e.g. after the companion was reinstalled with a new certificate).
        val known = saved(target)
        val lan = tester.testCompanion(
            host = target.host,
            token = token,
            pairCode = pairCode,
            device = identity.deviceInfo(),
            tlsPort = target.tlsPort ?: known?.tlsPort,
            certPin = known?.certPin.takeIf { pairCode == null },
        )
        // Same rule as the live session: LAN first, then the bonded PC over RFCOMM.
        val lanDown = lan is TestResult.Failed && !lan.error.isTerminalHandshakeError
        val bluetoothUsable = bt != null && settings.settings.value.bluetoothFallback && bluetooth.status(bt) == BluetoothStatus.READY
        if (lanDown && bluetoothUsable) {
            val viaBt = helloBluetooth(bt, token, pairCode)
            // A refusal over Bluetooth (pairing required…) is the answer; a dead RFCOMM link is not.
            return if (viaBt is TestResult.Failed && !viaBt.error.isTerminalHandshakeError) lan else viaBt
        }
        return lan
    }

    private suspend fun helloBluetooth(address: String, token: String?, pairCode: String?): TestResult =
        tester.testCompanionBluetooth(address = address, token = token, pairCode = pairCode, device = identity.deviceInfo())

    override suspend fun forgetToken(target: PairingTarget) {
        saved(target)?.let { secrets.setCompanionToken(it.id, null) }
    }

    override suspend fun connect(target: PairingTarget, welcome: TestResult.CompanionOk, newToken: String?): SavedServer {
        // The GUID from `welcome` also finds the entry of a PC paired over Wi-Fi when it is now
        // reached over Bluetooth (and vice versa): one saved PC, one token.
        val existing = saved(target, pcId = welcome.pcId ?: target.pcId)
        val server = mergePairedServer(existing, target, welcome, now = System.currentTimeMillis())
        if (newToken != null) secrets.setCompanionToken(server.id, newToken)
        controller.connect(server) // also saves the server and marks it last used
        if (newToken != null) controller.announcePaired(server)
        return server
    }
}

/**
 * The saved entry after connecting to [target]: the companion's GUID becomes the id of a new entry,
 * a name the user chose is kept, a LAN address is updated (DHCP may have moved the PC) but never
 * erased by a Bluetooth connection, the Bluetooth address comes from `welcome` (or the bonded device).
 */
internal fun mergePairedServer(
    existing: SavedServer?,
    target: PairingTarget,
    welcome: TestResult.CompanionOk,
    now: Long,
): SavedServer {
    val pcId = welcome.pcId ?: target.pcId ?: existing?.pcId
    val id = existing?.id ?: pcId ?: UUID.randomUUID().toString()
    val keptName = existing?.name?.takeIf { it.isNotBlank() && it != existing.host }
    val viaLan = target.host.isNotBlank()
    return (existing ?: SavedServer(id = id, name = "", host = "")).copy(
        name = keptName ?: welcome.name.ifBlank { target.name },
        host = if (viaLan) target.host else existing?.host.orEmpty(),
        wsPort = if (viaLan) target.wsPort else existing?.wsPort ?: target.wsPort,
        pcId = pcId,
        btAddress = BtAddress.normalize(welcome.bt) ?: BtAddress.normalize(target.btAddress) ?: existing?.btAddress,
        // Pairing again (a new code) is how a PC with a new certificate is trusted again.
        tlsPort = welcome.tlsPort ?: existing?.tlsPort,
        certPin = welcome.certPin ?: existing?.certPin,
        lastUsedAt = now,
    )
}
