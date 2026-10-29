package app.sofabird.ui.connection

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.sofabird.data.companion.BluetoothStatus
import app.sofabird.data.companion.BluetoothSupport
import app.sofabird.data.companion.BondedDevice
import app.sofabird.data.companion.sortedForPicker
import app.sofabird.data.discovery.DiscoveredHost
import app.sofabird.data.discovery.DiscoveryRepository
import app.sofabird.data.player.CompanionLink
import app.sofabird.data.player.PairingFlow
import app.sofabird.data.player.PairingState
import app.sofabird.data.player.PairingTarget
import app.sofabird.data.player.PlayerController
import app.sofabird.data.secrets.SecretStore
import app.sofabird.data.servers.ServerRepository
import app.sofabird.domain.model.SavedServer
import app.sofabird.domain.model.findCompanion
import app.sofabird.protocol.companion.COMPANION_WS_PORT
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
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

/** Manual-entry form ("Other ways to connect"): the companion's address; connecting starts the pairing flow. */
data class ConnectionForm(
    val host: String = "",
    val port: String = COMPANION_WS_PORT.toString(),
    val name: String = "",
    /** When editing an existing saved server. */
    val serverId: String? = null,
    /** Token already stored for this server; shown as "paired". */
    val hasToken: Boolean = false,
)

/** "Connect via Bluetooth": what the picker shows. */
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
    val scanning: Boolean = false,
    /** Saved PCs that are not in [nearby] right now. */
    val saved: List<SavedServer> = emptyList(),
    val activeServerId: String? = null,
    val activeServer: SavedServer? = null,
    val connection: ConnectionState = ConnectionState.Disconnected,
    val form: ConnectionForm = ConnectionForm(),
    val showInstructions: Boolean = false,
    /** "Other ways to connect" expanded. */
    val otherOptionsExpanded: Boolean = false,
    /** Discovery ran for a while without finding a companion. */
    val searchTimedOut: Boolean = false,
    val pairing: PairingState = PairingState.Idle,
    val btPicker: BtPickerState = BtPickerState.Hidden,
)

class ConnectionViewModel(
    private val controller: PlayerController,
    private val discovery: DiscoveryRepository,
    private val servers: ServerRepository,
    private val secrets: SecretStore,
    link: CompanionLink,
    private val bluetooth: BluetoothSupport,
) : ViewModel() {

    private val pairingFlow = PairingFlow(viewModelScope, link)

    private val form = MutableStateFlow(ConnectionForm())
    private val showInstructions = MutableStateFlow(false)

    /** `null` until the user opens/closes "other ways" themselves; then their choice wins. */
    private val otherOptionsChoice = MutableStateFlow<Boolean?>(null)
    private val searchTimedOut = MutableStateFlow(false)
    private val btPicker = MutableStateFlow<BtPickerState>(BtPickerState.Hidden)
    private var searchTimerJob: Job? = null

    /** The user did something themselves: never auto-start the pairing after that. */
    private var userActed = false

    private data class Discovery(val hosts: List<DiscoveredHost>, val scanning: Boolean)
    private data class Local(
        val form: ConnectionForm,
        val instructions: Boolean,
        val otherChoice: Boolean?,
        val timedOut: Boolean,
        val btPicker: BtPickerState,
    )

    val uiState: StateFlow<ConnectionUiState> = combine(
        combine(discovery.hosts, discovery.scanning) { hosts, scanning ->
            Discovery(hosts.values.sortedBy { it.displayName.lowercase() }, scanning)
        },
        servers.serversState,
        combine(controller.activeServer, controller.connection) { a, c -> a to c },
        combine(form, showInstructions, otherOptionsChoice, searchTimedOut, btPicker) { f, i, o, s, b -> Local(f, i, o, s, b) },
        pairingFlow.state,
    ) { d, saved, (active, connection), local, pairing ->
        val nearby = d.hosts.map { host ->
            val match = saved.findCompanion(host.host, host.wsPort, host.pcId)
            NearbyPc(host, match, paired = match?.let { secrets.companionToken(it.id) } != null)
        }
        val nearbyIds = nearby.mapNotNull { it.saved?.id }.toSet()
        ConnectionUiState(
            nearby = nearby,
            scanning = d.scanning,
            saved = saved.filterNot { it.id in nearbyIds }.sortedByDescending { it.lastUsedAt },
            activeServerId = active?.id,
            activeServer = active,
            connection = connection,
            form = local.form,
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
            discovery.hosts.first { it.isNotEmpty() }
            delay(AUTO_SETTLE_MS) // give other PCs a moment to answer too
            val companions = discovery.hosts.value.values
            if (companions.size == 1 && !userActed && pairingFlow.state.value is PairingState.Idle) {
                pairingFlow.start(companions.single().toTarget(), auto = true)
            }
        }
    }

    /** The active companion rejected our token (e.g. the PC forgot the phone): ask for a code right away. */
    private fun resumePairingOfActiveServer() {
        val active = controller.activeServer.value ?: return
        val c = controller.connection.value
        if (c is ConnectionState.Failed && c.error == ConnectionError.PairingRequired) {
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

    /** Forget a PC: its token and the saved entry. */
    fun forget(server: SavedServer) {
        userActed = true
        viewModelScope.launch {
            if (controller.activeServer.value?.id == server.id) controller.disconnect()
            secrets.removeAll(server.id)
            servers.remove(server.id)
        }
    }

    // ---------------------------------------------------------------- Bluetooth

    /** "Connect via Bluetooth": permission → radio on → list of bonded devices. */
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
    }

    fun edit(server: SavedServer) {
        userActed = true
        otherOptionsChoice.value = true
        form.value = ConnectionForm(
            host = server.host,
            port = server.wsPort.toString(),
            name = server.name,
            serverId = server.id,
            hasToken = secrets.companionToken(server.id) != null,
        )
    }

    /** The same connect-or-pair flow as for a found PC; a new name for a saved PC is kept. */
    fun saveAndConnect() {
        val f = form.value
        val port = f.port.toIntOrNull() ?: return
        if (f.host.isBlank()) return
        userActed = true
        viewModelScope.launch {
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

    fun connectSaved(server: SavedServer) {
        userActed = true
        pairingFlow.start(server.toTarget())
    }

    fun disconnect() {
        viewModelScope.launch { controller.disconnect() }
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
