package dev.vlcremote.app.ui.connection

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.discovery.DiscoveredHost
import dev.vlcremote.app.data.discovery.DiscoveryRepository
import dev.vlcremote.app.data.player.ConnectionTester
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.player.TestResult
import dev.vlcremote.app.data.secrets.SecretStore
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.protocol.companion.COMPANION_WS_PORT
import dev.vlcremote.protocol.companion.DeviceInfo
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.util.UUID

/** Manual-entry form. [mode] decides which secret is used (password vs pairing code). */
data class ConnectionForm(
    val host: String = "",
    val port: String = SavedServer.DEFAULT_HTTP_PORT.toString(),
    val mode: ServerMode = ServerMode.DIRECT,
    val password: String = "",
    val pairCode: String = "",
    val name: String = "",
    /** When editing an existing saved server. */
    val serverId: String? = null,
    /** Token already stored for this server (companion); shown as "paired". */
    val hasToken: Boolean = false,
)

sealed class TestState {
    data object Idle : TestState()
    data object Running : TestState()
    data class Ok(val result: TestResult) : TestState()
    data class Failed(val error: ConnectionError) : TestState()
}

data class ConnectionUiState(
    val discovered: List<DiscoveredHost> = emptyList(),
    val scanning: Boolean = false,
    val sweeping: Boolean = false,
    val saved: List<SavedServer> = emptyList(),
    val activeServerId: String? = null,
    val connection: ConnectionState = ConnectionState.Disconnected,
    val form: ConnectionForm = ConnectionForm(),
    val test: TestState = TestState.Idle,
    val showInstructions: Boolean = false,
)

class ConnectionViewModel(
    private val controller: PlayerController,
    private val discovery: DiscoveryRepository,
    private val tester: ConnectionTester,
    private val servers: ServerRepository,
    private val secrets: SecretStore,
    private val settings: dev.vlcremote.app.data.settings.SettingsRepository,
) : ViewModel() {

    private val form = MutableStateFlow(ConnectionForm())
    private val test = MutableStateFlow<TestState>(TestState.Idle)
    private val showInstructions = MutableStateFlow(false)
    private var testJob: Job? = null

    val uiState: StateFlow<ConnectionUiState> = combine(
        combine(discovery.hosts, discovery.scanning, discovery.sweeping) { hosts, scanning, sweeping ->
            Triple(hosts.values.sortedWith(compareByDescending<DiscoveredHost> { it.hasCompanion }.thenBy { it.displayName }), scanning, sweeping)
        },
        servers.serversState,
        controller.activeServer,
        controller.connection,
        combine(form, test, showInstructions) { f, t, i -> Triple(f, t, i) },
    ) { (hosts, scanning, sweeping), saved, active, connection, (f, t, instructions) ->
        ConnectionUiState(
            discovered = hosts,
            scanning = scanning,
            sweeping = sweeping,
            saved = saved.sortedByDescending { it.lastUsedAt },
            activeServerId = active?.id,
            connection = connection,
            form = f,
            test = t,
            showInstructions = instructions,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), ConnectionUiState())

    fun startDiscovery() = discovery.start()
    fun stopDiscovery() = discovery.stop()
    fun sweepSubnet() = discovery.sweepSubnet()

    fun toggleInstructions() = showInstructions.update { !it }

    // ---------------------------------------------------------------- form

    fun updateForm(block: (ConnectionForm) -> ConnectionForm) {
        form.update(block)
        test.value = TestState.Idle
    }

    /** Pre-fill the form from a discovered host. */
    fun pick(host: DiscoveredHost) {
        val existing = servers.serversState.value.firstOrNull { it.host == host.host && it.mode == (if (host.hasCompanion) ServerMode.COMPANION else ServerMode.DIRECT) }
        form.value = ConnectionForm(
            host = host.host,
            port = if (host.hasCompanion) host.wsPort.toString() else host.httpPort.toString(),
            mode = if (host.hasCompanion) ServerMode.COMPANION else ServerMode.DIRECT,
            name = host.displayName,
            serverId = existing?.id,
            password = existing?.let { secrets.vlcPassword(it.id) }.orEmpty(),
            hasToken = existing?.let { secrets.companionToken(it.id) } != null,
        )
        test.value = TestState.Idle
    }

    fun edit(server: SavedServer) {
        form.value = ConnectionForm(
            host = server.host,
            port = (if (server.isCompanion) server.wsPort else server.httpPort).toString(),
            mode = server.mode,
            name = server.name,
            serverId = server.id,
            password = secrets.vlcPassword(server.id).orEmpty(),
            hasToken = secrets.companionToken(server.id) != null,
        )
        test.value = TestState.Idle
    }

    fun setMode(mode: ServerMode) = updateForm { f ->
        val defaultPort = if (mode == ServerMode.COMPANION) COMPANION_WS_PORT else SavedServer.DEFAULT_HTTP_PORT
        val keepPort = f.port.toIntOrNull()?.let { it != SavedServer.DEFAULT_HTTP_PORT && it != COMPANION_WS_PORT } == true
        f.copy(mode = mode, port = if (keepPort) f.port else defaultPort.toString())
    }

    // ---------------------------------------------------------------- test / connect

    fun testConnection() {
        val f = form.value
        val port = f.port.toIntOrNull() ?: return
        if (f.host.isBlank()) return
        testJob?.cancel()
        test.value = TestState.Running
        testJob = viewModelScope.launch {
            val result = when (f.mode) {
                ServerMode.DIRECT -> tester.testDirect(f.host.trim(), port, f.password)
                ServerMode.COMPANION -> {
                    val token = f.serverId?.let { secrets.companionToken(it) }
                    tester.testCompanion(
                        host = f.host.trim(),
                        wsPort = port,
                        token = token,
                        pairCode = f.pairCode.takeIf { it.isNotBlank() },
                        device = DeviceInfo(name = android.os.Build.MODEL ?: "Android", id = secrets.deviceId()),
                    )
                }
            }
            test.value = when (result) {
                is TestResult.Failed -> TestState.Failed(result.error)
                else -> TestState.Ok(result)
            }
            if (result is TestResult.CompanionOk && result.token != null) {
                // Pairing succeeded during the test: keep the token right away.
                val server = buildServer(f, result)
                secrets.setCompanionToken(server.id, result.token)
                servers.upsert(server, markLastUsed = false)
                form.update { it.copy(serverId = server.id, hasToken = true, pairCode = "", name = result.name) }
            }
        }
    }

    /** Save the form and connect; secrets go to the encrypted store. */
    fun saveAndConnect(onConnected: () -> Unit) {
        val f = form.value
        if (f.host.isBlank() || f.port.toIntOrNull() == null) return
        viewModelScope.launch {
            val server = buildServer(f, (test.value as? TestState.Ok)?.result)
            when (f.mode) {
                ServerMode.DIRECT -> secrets.setVlcPassword(server.id, f.password)
                ServerMode.COMPANION -> {
                    if (f.pairCode.isNotBlank() && !f.hasToken) controller.setPairCode(server.id, f.pairCode.trim())
                }
            }
            servers.upsert(server)
            controller.connect(server)
            onConnected()
        }
    }

    fun connectSaved(server: SavedServer, onConnected: () -> Unit) {
        viewModelScope.launch {
            controller.connect(server)
            onConnected()
        }
    }

    fun forget(server: SavedServer) {
        viewModelScope.launch {
            if (controller.activeServer.value?.id == server.id) controller.disconnect()
            secrets.removeAll(server.id)
            servers.remove(server.id)
        }
    }

    fun disconnect() {
        viewModelScope.launch { controller.disconnect() }
    }

    private fun buildServer(f: ConnectionForm, result: TestResult?): SavedServer {
        val id = f.serverId ?: UUID.randomUUID().toString()
        val existing = servers.serversState.value.firstOrNull { it.id == id }
        val port = f.port.toIntOrNull() ?: SavedServer.DEFAULT_HTTP_PORT
        val name = f.name.ifBlank { (result as? TestResult.CompanionOk)?.name ?: f.host.trim() }
        return SavedServer(
            id = id,
            name = name,
            host = f.host.trim(),
            httpPort = if (f.mode == ServerMode.DIRECT) port else existing?.httpPort ?: SavedServer.DEFAULT_HTTP_PORT,
            wsPort = if (f.mode == ServerMode.COMPANION) port else existing?.wsPort ?: COMPANION_WS_PORT,
            mode = f.mode,
            lastUsedAt = System.currentTimeMillis(),
            lastBrowseUri = existing?.lastBrowseUri,
            favoriteFolders = existing?.favoriteFolders.orEmpty(),
            pcId = (result as? TestResult.CompanionOk)?.pcId ?: existing?.pcId,
        )
    }

    override fun onCleared() {
        discovery.stop()
        super.onCleared()
    }
}
