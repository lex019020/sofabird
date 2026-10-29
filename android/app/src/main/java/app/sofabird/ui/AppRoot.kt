package app.sofabird.ui

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material.icons.outlined.PlayCircle
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.ScaffoldDefaults
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavDestination.Companion.hierarchy
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import app.sofabird.R
import app.sofabird.data.player.PlayerController
import app.sofabird.data.player.PlayerEvent
import app.sofabird.data.servers.ServerRepository
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.domain.permissions.PermissionRegistry
import app.sofabird.protocol.model.ConnectionError
import app.sofabird.protocol.model.ConnectionState
import app.sofabird.protocol.model.TransportKind
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.StatusDot
import app.sofabird.ui.common.commandErrorMessage
import app.sofabird.ui.common.message
import app.sofabird.ui.connection.ConnectionScreen
import app.sofabird.ui.history.HistoryScreen
import app.sofabird.ui.history.ResumePromptHost
import app.sofabird.ui.library.LibraryScreen
import app.sofabird.ui.media.ShowScreen
import app.sofabird.ui.permissions.PermissionsScreen
import app.sofabird.ui.permissions.isGranted
import app.sofabird.ui.playlist.PlaylistScreen
import app.sofabird.ui.remote.RemoteScreen
import app.sofabird.ui.settings.SettingsScreen
import app.sofabird.ui.update.UpdatePromptHost
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.koin.compose.koinInject

object Routes {
    const val REMOTE = "remote"
    const val LIBRARY = "library"
    const val PLAYLIST = "playlist"
    const val CONNECTION = "connection"
    const val SETTINGS = "settings"
    const val HISTORY = "history"
    const val SHOW = "show/{key}"
    fun show(key: String) = "show/" + android.net.Uri.encode(key)
    val tabs = listOf(REMOTE, LIBRARY, PLAYLIST)
}

@Composable
fun AppRoot(forcePermissionsScreen: Boolean = false) {
    val controller: PlayerController = koinInject()
    val navController = rememberNavController()
    val snackbar = remember { SnackbarHostState() }
    val context = LocalContext.current

    val connection by controller.connection.collectAsStateWithLifecycle()
    val server by controller.activeServer.collectAsStateWithLifecycle()
    val backStack by navController.currentBackStackEntryAsState()
    val route = backStack?.destination?.route
    val settings: SettingsRepository = koinInject()
    val scope = rememberCoroutineScope()

    // First-run "Permissions" screen (and once more after an update that added a permission): decided
    // from the stored screen version before anything else is shown. null = still deciding.
    var showPermissions by rememberSaveable { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(Unit) {
        if (showPermissions != null) return@LaunchedEffect
        val sdk = Build.VERSION.SDK_INT
        val show = if (forcePermissionsScreen) {
            PermissionRegistry.applicable(sdk).isNotEmpty()
        } else {
            PermissionRegistry.shouldShowScreen(sdk, settings.permissionsScreenVersion()) { isGranted(context, it) }
        }
        // Nothing to ask (all granted / old Android): never show it later just because a permission was revoked.
        if (!show) settings.markPermissionsScreenSeen(PermissionRegistry.screenVersion)
        showPermissions = show
    }

    // Fallback after "Not now" on the permissions screen — Android 13+: ask for notifications once we are
    // connected (the service shows a media card). Not over the permissions screen, and not again if the
    // user has already answered the system dialog (Settings → Permissions can still grant it).
    val notifLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    LaunchedEffect(connection.isConnected, showPermissions) {
        val permission = Manifest.permission.POST_NOTIFICATIONS
        val askable = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            !isGranted(context, permission) && permission !in settings.requestedPermissions.value
        if (showPermissions == false && connection.isConnected && askable) {
            settings.noteRequested(listOf(permission))
            notifLauncher.launch(permission)
        }
    }

    // Snackbars for failed commands / pairing; volume overlay text for hardware keys.
    LaunchedEffect(Unit) {
        controller.events.collectLatest { event ->
            when (event) {
                is PlayerEvent.CommandFailed -> snackbar.showSnackbar(event.error.commandErrorMessage(context))
                is PlayerEvent.SystemCommandFailed -> snackbar.showSnackbar(event.error.commandErrorMessage(context))
                is PlayerEvent.VolumeChanged -> snackbar.showSnackbar(context.getString(R.string.volume_percent, event.percent))
                is PlayerEvent.Paired -> snackbar.showSnackbar(context.getString(R.string.paired_with, event.server.displayName))
                is PlayerEvent.RequestFailed -> snackbar.showSnackbar(event.error.commandErrorMessage(context))
            }
        }
    }

    // Quiet reconnect notice (never a dialog).
    LaunchedEffect(connection) {
        val c = connection
        if (c is ConnectionState.Connecting && c.attempt > 0 && c.lastError != null) {
            snackbar.showSnackbar(context.getString(R.string.reconnecting_detail, c.lastError!!.message(context)))
        }
    }

    // Wi-Fi ↔ Bluetooth switch of a live companion session: a quiet notice, never a dialog.
    val transport = (connection as? ConnectionState.Connected)?.transport
    val lastTransport = remember { mutableStateOf<TransportKind?>(null) }
    LaunchedEffect(transport) {
        if (transport == null) return@LaunchedEffect
        val previous = lastTransport.value
        lastTransport.value = transport
        if (previous != null && previous != transport) {
            snackbar.showSnackbar(
                context.getString(
                    if (transport == TransportKind.BLUETOOTH) R.string.transport_switched_bluetooth else R.string.transport_switched_wifi,
                ),
            )
        }
    }

    // First launch without any saved PC: start on the connection screen (once, not a loop),
    // after the permissions screen.
    val servers: ServerRepository = koinInject()
    LaunchedEffect(showPermissions) {
        if (showPermissions == false && servers.servers.first().isEmpty()) {
            navController.navigate(Routes.CONNECTION) { launchSingleTop = true }
        }
    }

    // "Continue from …?" — modal over any screen while the PC waits paused for the answer.
    ResumePromptHost()

    when (showPermissions) {
        null -> {
            Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background))
            return
        }
        true -> {
            PermissionsScreen(
                onDone = {
                    scope.launch { settings.markPermissionsScreenSeen(PermissionRegistry.screenVersion) }
                    showPermissions = false
                },
            )
            return
        }
        false -> Unit
    }

    // "Update available" — once per version, not over the permissions screen.
    UpdatePromptHost()

    val showChrome = route in Routes.tabs
    Scaffold(
        // Screens without the shared bars (settings, history, a series…) have their own Scaffold and TopAppBar that
        // take the system bars into account: insetting them here too doubled the gap under the status bar.
        contentWindowInsets = if (showChrome) ScaffoldDefaults.contentWindowInsets else WindowInsets(0),
        topBar = {
            if (showChrome) {
                ConnectionTopBar(
                    connection = connection,
                    hostName = server?.displayName,
                    showTransport = server?.isCompanion == true,
                    onClick = { navController.navigate(Routes.CONNECTION) { launchSingleTop = true } },
                    onSettings = { navController.navigate(Routes.SETTINGS) { launchSingleTop = true } },
                )
            }
        },
        bottomBar = {
            if (showChrome) {
                NavigationBar {
                    val current = backStack?.destination
                    Routes.tabs.forEach { tab ->
                        val selected = current?.hierarchy?.any { it.route == tab } == true
                        NavigationBarItem(
                            selected = selected,
                            onClick = {
                                navController.navigate(tab) {
                                    popUpTo(navController.graph.findStartDestination().id) { saveState = true }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            },
                            icon = {
                                when (tab) {
                                    Routes.REMOTE -> Icon(Icons.Outlined.PlayCircle, null)
                                    Routes.LIBRARY -> Icon(Icons.Default.Folder, null)
                                    else -> Icon(Icons.AutoMirrored.Filled.QueueMusic, null)
                                }
                            },
                            label = {
                                LabelText(
                                    when (tab) {
                                        Routes.REMOTE -> stringResource(R.string.tab_remote)
                                        Routes.LIBRARY -> stringResource(R.string.tab_library)
                                        else -> stringResource(R.string.tab_playlist)
                                    },
                                )
                            },
                        )
                    }
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = Routes.REMOTE,
            modifier = Modifier.padding(padding),
        ) {
            composable(Routes.REMOTE) { RemoteScreen(onOpenConnection = { navController.navigate(Routes.CONNECTION) }) }
            composable(Routes.LIBRARY) {
                LibraryScreen(
                    onPlayed = { navController.navigate(Routes.REMOTE) },
                    onOpenHistory = { navController.navigate(Routes.HISTORY) { launchSingleTop = true } },
                    onOpenShow = { key -> navController.navigate(Routes.show(key)) { launchSingleTop = true } },
                )
            }
            composable(Routes.HISTORY) {
                HistoryScreen(
                    onBack = { navController.popBackStack() },
                    // Like a tap in the Library: back to the remote (History closes).
                    onPlayed = {
                        navController.popBackStack()
                        navController.navigate(Routes.REMOTE)
                    },
                )
            }
            composable(Routes.SHOW) { entry ->
                ShowScreen(
                    key = entry.arguments?.getString("key").orEmpty(),
                    onBack = { navController.popBackStack() },
                    onPlayed = {
                        navController.popBackStack()
                        navController.navigate(Routes.REMOTE)
                    },
                )
            }
            composable(Routes.PLAYLIST) { PlaylistScreen() }
            composable(Routes.CONNECTION) {
                ConnectionScreen(
                    onConnected = {
                        navController.navigate(Routes.REMOTE) {
                            popUpTo(Routes.REMOTE) { inclusive = true }
                        }
                    },
                    onBack = { if (!navController.popBackStack()) navController.navigate(Routes.REMOTE) },
                )
            }
            composable(Routes.SETTINGS) { SettingsScreen(onBack = { navController.popBackStack() }) }
        }
    }
}

@Composable
private fun ConnectionTopBar(
    connection: ConnectionState,
    hostName: String?,
    showTransport: Boolean,
    onClick: () -> Unit,
    onSettings: () -> Unit,
) {
    val (color, text) = connectionSummary(connection)
    TopAppBar(
        title = {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(onClick = onClick),
            ) {
                if (connection is ConnectionState.Connecting) {
                    CircularProgressIndicator(modifier = Modifier.width(14.dp), strokeWidth = 2.dp)
                } else {
                    StatusDot(color)
                }
                Spacer(Modifier.width(10.dp))
                // The app bar has a fixed height: both lines must fit it at any font scale.
                Column {
                    LabelText(hostName ?: stringResource(R.string.no_server), style = MaterialTheme.typography.titleMedium)
                    LabelText(text, style = MaterialTheme.typography.bodySmall)
                }
            }
        },
        actions = {
            val transport = connection.transportOrNull
            if (showTransport && transport != null) TransportIndicator(transport)
            IconButton(onClick = onSettings) { Icon(Icons.Default.Settings, stringResource(R.string.settings)) }
        },
        colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.surface),
    )
}

/** Small "Wi-Fi" / "Bluetooth" badge: which link carries the companion session. */
@Composable
private fun TransportIndicator(transport: TransportKind) {
    val bluetooth = transport == TransportKind.BLUETOOTH
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .background(
                if (bluetooth) MaterialTheme.colorScheme.tertiaryContainer else MaterialTheme.colorScheme.secondaryContainer,
                RoundedCornerShape(8.dp),
            )
            .padding(horizontal = 8.dp, vertical = 3.dp),
    ) {
        val tint = if (bluetooth) MaterialTheme.colorScheme.onTertiaryContainer else MaterialTheme.colorScheme.onSecondaryContainer
        Icon(
            if (bluetooth) Icons.Default.Bluetooth else Icons.Default.Wifi,
            contentDescription = null,
            tint = tint,
            modifier = Modifier.size(14.dp),
        )
        Spacer(Modifier.width(4.dp))
        LabelText(
            stringResource(if (bluetooth) R.string.transport_bluetooth else R.string.transport_wifi),
            style = MaterialTheme.typography.labelSmall,
            color = tint,
        )
    }
}

@Composable
fun connectionSummary(connection: ConnectionState): Pair<Color, String> {
    val context = LocalContext.current
    return when (connection) {
        is ConnectionState.Disconnected -> Color.Gray to stringResource(R.string.state_disconnected)
        is ConnectionState.Connecting -> MaterialTheme.colorScheme.tertiary to when {
            connection.lastError == ConnectionError.PcAsleep -> stringResource(R.string.state_asleep)
            connection.attempt == 0 -> stringResource(R.string.state_connecting)
            else -> stringResource(R.string.state_reconnecting, connection.attempt)
        }
        is ConnectionState.Connected -> Color(0xFF4CAF50) to buildString {
            append(
                when (connection.level) {
                    2 -> context.getString(
                        R.string.connection_level_transport,
                        context.getString(R.string.level_companion),
                        context.getString(
                            if (connection.transport == TransportKind.BLUETOOTH) R.string.transport_bluetooth else R.string.transport_wifi,
                        ),
                    )
                    1 -> context.getString(R.string.level_lua)
                    else -> context.getString(R.string.level_http)
                },
            )
            connection.latencyMs?.let { append(" · ").append(context.getString(R.string.latency_ms, it)) }
        }
        is ConnectionState.Degraded -> Color(0xFFFFC107) to stringResource(R.string.state_degraded)
        is ConnectionState.Failed -> MaterialTheme.colorScheme.error to connection.error.message(context)
    }
}
