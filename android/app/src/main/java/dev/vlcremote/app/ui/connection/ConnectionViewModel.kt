package dev.vlcremote.app.ui.connection

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dev.vlcremote.app.data.companion.BluetoothStatus
import dev.vlcremote.app.data.companion.BluetoothSupport
import dev.vlcremote.app.data.companion.BondedDevice
import dev.vlcremote.app.data.companion.sortedForPicker
import dev.vlcremote.app.data.discovery.DiscoveredHost
import dev.vlcremote.app.data.discovery.DiscoveryRepository
import dev.vlcremote.app.data.player.CompanionLink
import dev.vlcremote.app.data.player.ConnectionTester
import dev.vlcremote.app.data.player.PairingFlow
import dev.vlcremote.app.data.player.PairingState
import dev.vlcremote.app.data.player.PairingTarget
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.player.TestResult
import dev.vlcremote.app.data.secrets.SecretStore
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.domain.model.SavedServer
import dev.vlcremote.app.domain.model.ServerMode
import dev.vlcremote.app.domain.model.findCompanion
import dev.vlcremote.protocol.companion.COMPANION_WS_PORT
import dev.vlcremote.protocol.model.ConnectionError
import dev.vlcremote.protocol.model.ConnectionState
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.util.UUID

/** Manual-entry form ("Другие способы подключения"). */
data class ConnectionForm(
    val host: String = "",
    val port: String = SavedServer.DEFAULT_HTTP_PORT.toString(),
    val mode: ServerMode = ServerMode.DIRECT,
    val password: String = "",
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

/** «Подключиться по Bluetooth»: what the picker shows. */
sealed interface BtPickerState {
    data object Hidden : BtPickerState

    /** Android 12+: ask for "Nearby devices" (rationale dialog first). */
    data object NeedsPermission : BtPickerState

    /** The user refused; only the app settings can grant it now. */
    data object PermissionDenied : BtPickerState

    data object Off : BtPickerState

    data object Unsupported : BtPickerState

    /** Bonded devices, computers first. */
    data class Devices(val devices: List<BondedDevice>) : BtPickerState
}

/** A companion found on the network, with what we know about it locally. */
data class NearbyPc(
    val host: DiscoveredHost,
    /** Saved server this PC corresponds to, if any. */
    val saved: SavedServer?,
    /** We hold a token for it: tapping connects straight away. */
    val paired: Boolean,
)

data class ConnectionUiState(
    /** Companions found nearby (the main way to connect). */
    val nearby: List<NearbyPc> = emptyList(),
    /** Plain VLC web interfaces found by the subnet sweep (no companion). */
    val otherHosts: List<DiscoveredHost> = emptyList(),
    val scanning: Boolean = false,
    val sweeping: Boolean = false,
    /** Saved PCs that are not in [nearby] right now. */
    val saved: List<SavedServer> = emptyList(),
    val activeServerId: String? = null,
    val activeServer: SavedServer? = null,
    val connection: ConnectionState = ConnectionState.Disconnected,
    val form: ConnectionForm = ConnectionForm(),
    val test: TestState = TestState.Idle,
    val showInstructions: Boolean = false,
    /** "Другие способы подключения" expanded. */
    val otherOptionsExpanded: Boolean = false,
    /** Discovery ran for a while without finding a companion. */
    val searchTimedOut: Boolean = false,
    val pairing: PairingState = PairingState.Idle,
    val btPicker: BtPickerState = BtPickerState.Hidden,
)

class ConnectionViewModel(
    private val controller: PlayerController,
    private val discovery: DiscoveryRepository,
    private val tester: ConnectionTester,
    private val servers: ServerRepository,
    private val secrets: SecretStore,
    link: CompanionLink,
    private val bluetooth: BluetoothSupport,
) : ViewModel() {

    private val pairingFlow = PairingFlow(viewModelScope, link)

    private val form = MutableStateFlow(ConnectionForm())
    private val test = MutableStateFlow<TestState>(TestState.Idle)
    private val showInstructions = MutableStateFlow(false)

    /** `null` until the user opens/closes "other ways" themselves; then their choice wins. */
    private val otherOptionsChoice = MutableStateFlow<Boolean?>(null)
    private val searchTimedOut = MutableStateFlow(false)
    private val btPicker = MutableStateFlow<BtPickerState>(BtPickerState.Hidden)
    private var testJob: Job? = null
    private var searchTimerJob: Job? = null

    /** The user did something themselves: never auto-start the pairing after that. */
    private var userActed = false

    private data class Discovery(val hosts: List<DiscoveredHost>, val scanning: Boolean, val sweeping: Boolean)
    private data class Local(
        val form: ConnectionForm,
        val test: TestState,
        val instructions: Boolean,
        val otherChoice: Boolean?,
        val timedOut: Boolean,
        val btPicker: BtPickerState,
    )

    val uiState: StateFlow<ConnectionUiState> = combine(
        combine(discovery.hosts, discovery.scanning, discovery.sweeping) { hosts, scanning, sweeping ->
            Discovery(hosts.values.sortedBy { it.displayName.lowercase() }, scanning, sweeping)
        },
        servers.serversState,
        combine(controller.activeServer, controller.connection) { a, c -> a to c },
        combine(
            combine(form, test) { f, t -> f to t },
            showInstructions,
            otherOptionsChoice,
            searchTimedOut,
            btPicker,
        ) { (f, t), i, o, s, b -> Local(f, t, i, o, s, b) },
        pairingFlow.state,
    ) { d, saved, (active, connection), local, pairing ->
        val nearby = d.hosts.filter { it.hasCompanion }.map { host ->
            val match = saved.findCompanion(host.host, host.wsPort, host.pcId)
            NearbyPc(host, match, paired = match?.let { secrets.companionToken(it.id) } != null)
        }
        val nearbyIds = nearby.mapNotNull { it.saved?.id }.toSet()
        ConnectionUiState(
            nearby = nearby,
            otherHosts = d.hosts.filterNot { it.hasCompanion },
            scanning = d.scanning,
            sweeping = d.sweeping,
            saved = saved.filterNot { it.id in nearbyIds }.sortedByDescending { it.lastUsedAt },
            activeServerId = active?.id,
            activeServer = active,
            connection = connection,
            form = local.form,
            test = local.test,
            showInstructions = local.instructions,
            otherOptionsExpanded = local.otherChoice ?: (local.timedOut && nearby.isEmpty()),
            searchTimedOut = local.timedOut && nearby.isEmpty(),
            pairing = pairing,
            btPicker = local.btPicker,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), ConnectionUiState())

    init {
        autoStartOnFirstLaunch()
        resumePairingOfActiveServer()
    }

    /**
     * First launch (nothing saved) and exactly one companion around: start connecting to it right
     * away — the code pops up on the TV, the phone asks for it. With saved PCs the app instead
     * reconnects to the last one at start-up ([PlayerController.autoConnectLast]).
     */
    private fun autoStartOnFirstLaunch() {
        viewModelScope.launch {
            if (servers.servers.first().isNotEmpty()) return@launch
            discovery.hosts.first { hosts -> hosts.values.any { it.hasCompanion } }
            delay(AUTO_SETTLE_MS) // give other PCs a moment to answer too
            val companions = discovery.hosts.value.values.filter { it.hasCompanion }
            if (companions.size == 1 && !userActed && pairingFlow.state.value is PairingState.Idle) {
                pairingFlow.start(companions.single().toTarget(), auto = true)
            }
        }
    }

    /** The active companion rejected our token (e.g. the PC forgot the phone): ask for a code right away. */
    private fun resumePairingOfActiveServer() {
        val active = controller.activeServer.value ?: return
        val c = controller.connection.value
        if (active.isCompanion && c is ConnectionState.Failed && c.error == ConnectionError.PairingRequired) {
            pairingFlow.start(active.toTarget())
        }
    }

    fun startDiscovery() {
        discovery.start()
        if (searchTimerJob == null) {
            searchTimerJob = viewModelScope.launch {
                delay(SEARCH_TIMEOUT_MS)
                searchTimedOut.value = true
            }
        }
    }

    fun stopDiscovery() = discovery.stop()
    fun sweepSubnet() = discovery.sweepSubnet()

    fun toggleInstructions() = showInstructions.update { !it }

    fun setOtherOptionsExpanded(expanded: Boolean) {
        otherOptionsChoice.value = expanded
    }

    // ---------------------------------------------------------------- companion: connect / pair

    /** Tap on a PC card: connect if paired, otherwise the PC shows a code and we ask for it. */
    fun connectNearby(pc: NearbyPc) {
        userActed = true
        pairingFlow.start(pc.host.toTarget(pc.saved))
    }

    fun submitPairCode(code: String) = pairingFlow.submitCode(code)

    fun cancelPairing() {
        userActed = true
        pairingFlow.cancel()
    }

    fun retryPairing() = pairingFlow.retry()

    /** Called once the UI navigated to the remote after [PairingState.Done]. */
    fun pairingDoneHandled() = pairingFlow.consumeDone()

    /** Forget a PC: its token, password and the saved entry. */
    fun forget(server: SavedServer) {
        userActed = true
        viewModelScope.launch {
            if (controller.activeServer.value?.id == server.id) controller.disconnect()
            secrets.removeAll(server.id)
            servers.remove(server.id)
        }
    }

    // ---------------------------------------------------------------- Bluetooth

    /** «Подключиться по Bluetooth»: permission → radio on → list of bonded devices. */
    fun openBluetoothPicker() {
        userActed = true
        btPicker.value = when (bluetooth.radioStatus()) {
            BluetoothStatus.UNSUPPORTED -> BtPickerState.Unsupported
            BluetoothStatus.NO_PERMISSION -> BtPickerState.NeedsPermission
            BluetoothStatus.OFF -> BtPickerState.Off
            else -> BtPickerState.Devices(bluetooth.bondedDevices().sortedForPicker())
        }
    }

    fun onBluetoothPermissionResult(granted: Boolean) {
        if (granted) openBluetoothPicker() else btPicker.value = BtPickerState.PermissionDenied
    }

    fun onBluetoothEnableResult(enabled: Boolean) {
        if (enabled) openBluetoothPicker()
    }

    /** Back from the system Bluetooth settings: the bonded list may have changed. */
    fun refreshBluetoothPicker() {
        if (btPicker.value is BtPickerState.Devices || btPicker.value is BtPickerState.Off) openBluetoothPicker()
    }

    fun closeBluetoothPicker() {
        btPicker.value = BtPickerState.Hidden
    }

    /** Same connect-or-pair flow as over the LAN, the `hello` goes over RFCOMM. */
    fun connectBluetooth(device: BondedDevice) {
        userActed = true
        btPicker.value = BtPickerState.Hidden
        val saved = servers.serversState.value.findCompanion(host = "", wsPort = COMPANION_WS_PORT, pcId = null, btAddress = device.address)
        pairingFlow.start(
            PairingTarget(
                host = "",
                name = saved?.displayName ?: device.displayName,
                pcId = saved?.pcId,
                serverId = saved?.id,
                btAddress = device.address,
            ),
        )
    }

    // ---------------------------------------------------------------- manual form

    fun updateForm(block: (ConnectionForm) -> ConnectionForm) {
        userActed = true
        form.update(block)
        test.value = TestState.Idle
    }

    /** Pre-fill the form from a host found by the subnet sweep. */
    fun pick(host: DiscoveredHost) {
        userActed = true
        val mode = if (host.hasCompanion) ServerMode.COMPANION else ServerMode.DIRECT
        val existing = servers.serversState.value.firstOrNull { it.host == host.host && it.mode == mode }
        form.value = ConnectionForm(
            host = host.host,
            port = if (host.hasCompanion) host.wsPort.toString() else host.httpPort.toString(),
            mode = mode,
            name = host.displayName,
            serverId = existing?.id,
            password = existing?.let { secrets.vlcPassword(it.id) }.orEmpty(),
            hasToken = existing?.let { secrets.companionToken(it.id) } != null,
        )
        test.value = TestState.Idle
    }

    fun edit(server: SavedServer) {
        userActed = true
        otherOptionsChoice.value = true
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

    /** "Проверить" for the direct VLC mode (companions are checked by connecting). */
    fun testConnection() {
        val f = form.value
        val port = f.port.toIntOrNull() ?: return
        if (f.host.isBlank() || f.mode != ServerMode.DIRECT) return
        testJob?.cancel()
        test.value = TestState.Running
        testJob = viewModelScope.launch {
            test.value = when (val result = tester.testDirect(f.host.trim(), port, f.password)) {
                is TestResult.Failed -> TestState.Failed(result.error)
                else -> TestState.Ok(result)
            }
        }
    }

    /**
     * Direct mode: save (password to the encrypted store) and connect. Companion mode: go through
     * the same connect-or-pair flow as a found PC.
     */
    fun saveAndConnect(onConnected: () -> Unit) {
        val f = form.value
        val port = f.port.toIntOrNull() ?: return
        if (f.host.isBlank()) return
        userActed = true
        viewModelScope.launch {
            when (f.mode) {
                ServerMode.DIRECT -> {
                    val server = buildDirectServer(f, port)
                    secrets.setVlcPassword(server.id, f.password)
                    servers.upsert(server)
                    controller.connect(server)
                    onConnected()
                }
                ServerMode.COMPANION -> {
                    val name = f.name.trim()
                    if (f.serverId != null && name.isNotEmpty()) servers.update(f.serverId) { it.copy(name = name) }
                    pairingFlow.start(
                        PairingTarget(
                            host = f.host.trim(),
                            wsPort = port,
                            name = name.ifEmpty { f.host.trim() },
                            serverId = f.serverId,
                        ),
                    )
                }
            }
        }
    }

    fun connectSaved(server: SavedServer, onConnected: () -> Unit) {
        userActed = true
        if (server.isCompanion) {
            pairingFlow.start(server.toTarget())
            return
        }
        viewModelScope.launch {
            controller.connect(server)
            onConnected()
        }
    }

    fun disconnect() {
        viewModelScope.launch { controller.disconnect() }
    }

    private fun buildDirectServer(f: ConnectionForm, port: Int): SavedServer {
        val id = f.serverId ?: UUID.randomUUID().toString()
        val existing = servers.serversState.value.firstOrNull { it.id == id }
        return SavedServer(
            id = id,
            name = f.name.ifBlank { f.host.trim() },
            host = f.host.trim(),
            httpPort = port,
            wsPort = existing?.wsPort ?: COMPANION_WS_PORT,
            mode = ServerMode.DIRECT,
            lastUsedAt = System.currentTimeMillis(),
            lastBrowseUri = existing?.lastBrowseUri,
            favoriteFolders = existing?.favoriteFolders.orEmpty(),
            pcId = existing?.pcId,
        )
    }

    override fun onCleared() {
        discovery.stop()
        super.onCleared()
    }

    private companion object {
        /** Expand "other ways" when no companion answered in this time. */
        const val SEARCH_TIMEOUT_MS = 6_000L

        /** After the first companion answers, wait this long for others before auto-starting. */
        const val AUTO_SETTLE_MS = 1_500L
    }
}

private fun DiscoveredHost.toTarget(saved: SavedServer? = null) = PairingTarget(
    host = host,
    wsPort = wsPort,
    name = displayName,
    pcId = pcId ?: saved?.pcId,
    serverId = saved?.id,
)

private fun SavedServer.toTarget() = PairingTarget(
    host = host,
    wsPort = wsPort,
    name = displayName,
    pcId = pcId,
    serverId = id,
    btAddress = btAddress,
)
