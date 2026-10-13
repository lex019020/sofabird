package dev.vlcremote.app.data.player

import dev.vlcremote.app.data.secrets.SecretStore
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.domain.model.findCompanion
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
) : CompanionLink {

    private suspend fun saved(target: PairingTarget): SavedServer? =
        servers.servers.first().findCompanion(target.host, target.wsPort, target.pcId, target.serverId)

    override suspend fun storedToken(target: PairingTarget): String? =
        saved(target)?.let { secrets.companionToken(it.id) }

    override suspend fun hello(target: PairingTarget, token: String?, pairCode: String?): TestResult =
        tester.testCompanion(
            host = target.host,
            wsPort = target.wsPort,
            token = token,
            pairCode = pairCode,
            device = identity.deviceInfo(),
        )

    override suspend fun forgetToken(target: PairingTarget) {
        saved(target)?.let { secrets.setCompanionToken(it.id, null) }
    }

    override suspend fun connect(target: PairingTarget, welcome: TestResult.CompanionOk, newToken: String?): SavedServer {
        val existing = saved(target)
        val pcId = welcome.pcId ?: target.pcId ?: existing?.pcId
        val id = existing?.id ?: pcId ?: UUID.randomUUID().toString()
        // Keep a name the user chose; otherwise use what the PC calls itself.
        val keptName = existing?.name?.takeIf { it.isNotBlank() && it != existing.host }
        val server = (existing ?: SavedServer(id = id, name = "", host = target.host)).copy(
            name = keptName ?: welcome.name.ifBlank { target.name },
            host = target.host, // DHCP may have moved the PC
            wsPort = target.wsPort,
            mode = ServerMode.COMPANION,
            pcId = pcId,
            lastUsedAt = System.currentTimeMillis(),
        )
        if (newToken != null) secrets.setCompanionToken(server.id, newToken)
        controller.connect(server) // also saves the server and marks it last used
        if (newToken != null) controller.announcePaired(server)
        return server
    }
}
