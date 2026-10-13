package dev.vlcremote.app.ui

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
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
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavDestination.Companion.hierarchy
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import dev.vlcremote.app.R
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.player.PlayerEvent
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.ui.common.StatusDot
import dev.vlcremote.app.ui.common.commandErrorMessage
import dev.vlcremote.app.ui.common.message
import dev.vlcremote.app.ui.connection.ConnectionScreen
import dev.vlcremote.app.ui.library.LibraryScreen
import dev.vlcremote.app.ui.playlist.PlaylistScreen
import dev.vlcremote.app.ui.remote.RemoteScreen
import dev.vlcremote.app.ui.settings.SettingsScreen
import dev.vlcremote.protocol.model.ConnectionState
import dev.vlcremote.protocol.model.TransportKind
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.first
import org.koin.compose.koinInject

object Routes {
    const val REMOTE = "remote"
    const val LIBRARY = "library"
    const val PLAYLIST = "playlist"
    const val CONNECTION = "connection"
    const val SETTINGS = "settings"
    val tabs = listOf(REMOTE, LIBRARY, PLAYLIST)
}

@Composable
fun AppRoot() {
    val controller: PlayerController = koinInject()
    val navController = rememberNavController()
    val snackbar = remember { SnackbarHostState() }
    val context = LocalContext.current

    val connection by controller.connection.collectAsStateWithLifecycle()
    val server by controller.activeServer.collectAsStateWithLifecycle()
    val backStack by navController.currentBackStackEntryAsState()
    val route = backStack?.destination?.route

    // Android 13+: ask for notifications once we are connected (the service shows a media card).
    val notifLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    LaunchedEffect(connection.isConnected) {
        if (connection.isConnected && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            notifLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
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

    // First launch without any saved PC: start on the connection screen (once, not a loop).
    val servers: ServerRepository = koinInject()
    LaunchedEffect(Unit) {
        if (servers.servers.first().isEmpty()) {
            navController.navigate(Routes.CONNECTION) { launchSingleTop = true }
        }
    }

    val showChrome = route in Routes.tabs
    Scaffold(
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
                                Text(
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
            composable(Routes.LIBRARY) { LibraryScreen(onPlayed = { navController.navigate(Routes.REMOTE) }) }
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
    val (color, text) = connectionSummary(connection, hostName)
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
                Column {
                    Text(
                        text = hostName ?: stringResource(R.string.no_server),
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(text, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
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
        Text(
            stringResource(if (bluetooth) R.string.transport_bluetooth else R.string.transport_wifi),
            style = MaterialTheme.typography.labelSmall,
            color = tint,
        )
    }
}

@Composable
fun connectionSummary(connection: ConnectionState, hostName: String?): Pair<Color, String> {
    val context = LocalContext.current
    return when (connection) {
        is ConnectionState.Disconnected -> Color.Gray to stringResource(R.string.state_disconnected)
        is ConnectionState.Connecting -> MaterialTheme.colorScheme.tertiary to (
            if (connection.attempt == 0) stringResource(R.string.state_connecting)
            else stringResource(R.string.state_reconnecting, connection.attempt)
            )
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
