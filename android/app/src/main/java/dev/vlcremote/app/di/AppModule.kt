package dev.vlcremote.app.di

import dev.vlcremote.app.data.companion.AndroidCompanionTransports
import dev.vlcremote.app.data.companion.BluetoothSupport
import dev.vlcremote.app.data.companion.CompanionTransports
import dev.vlcremote.app.data.discovery.DiscoveryRepository
import dev.vlcremote.app.data.discovery.LocalNetwork
import dev.vlcremote.app.data.discovery.NsdDiscovery
import dev.vlcremote.app.data.discovery.SubnetScanner
import dev.vlcremote.app.data.discovery.UdpBeaconDiscovery
import dev.vlcremote.app.data.net.ArtAuth
import dev.vlcremote.app.data.net.HttpClients
import dev.vlcremote.app.data.player.AppVisibility
import dev.vlcremote.app.data.player.CompanionLink
import dev.vlcremote.app.data.player.CompanionPairingLink
import dev.vlcremote.app.data.player.ConnectionTester
import dev.vlcremote.app.data.player.DeviceIdentity
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.secrets.SecretStore
import dev.vlcremote.app.data.servers.CompanionServerSync
import dev.vlcremote.app.data.servers.ServerRepository
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.service.ServiceCoordinator
import dev.vlcremote.app.ui.connection.ConnectionViewModel
import dev.vlcremote.app.ui.library.LibraryViewModel
import dev.vlcremote.app.ui.playlist.PlaylistViewModel
import dev.vlcremote.app.ui.remote.RemoteViewModel
import dev.vlcremote.app.ui.settings.SettingsViewModel
import io.ktor.client.HttpClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import okhttp3.OkHttpClient
import org.koin.android.ext.koin.androidContext
import org.koin.core.module.dsl.viewModel
import org.koin.core.qualifier.named
import org.koin.dsl.module

val appModule = module {
    // Coordination scope: single-threaded (main) so backend bookkeeping needs no locks;
    // all network I/O runs on Ktor/OkHttp threads anyway.
    single(named("appScope")) { CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate) }

    single { AppVisibility().also { it.install() } }
    single { SettingsRepository(androidContext(), get(named("appScope"))) }
    single { ServerRepository(androidContext(), get(named("appScope"))) }
    single { SecretStore(androidContext()) }
    single { ArtAuth() }
    single<OkHttpClient> { HttpClients.okHttp(get<ArtAuth>().interceptor) }
    single<HttpClient> { HttpClients.ktor(get()) }

    single { LocalNetwork(androidContext()) }
    single { NsdDiscovery(androidContext()) }
    single { UdpBeaconDiscovery(get()) }
    single { SubnetScanner(get(), get()) }
    single { DiscoveryRepository(get(), get(), get(), get(), get(named("appScope"))) }

    single { DeviceIdentity(androidContext(), get()) }
    single { BluetoothSupport(androidContext()) }
    single<CompanionTransports> { AndroidCompanionTransports(get(), get(), get()) }
    single { CompanionServerSync(get(), get(), get(named("appScope"))) }

    single { ConnectionTester(get(), get()) }
    single { PlayerController(get(), get(), get(), get(), get(), get(), get(), get(), get(named("appScope"))) }
    single<CompanionLink> { CompanionPairingLink(get(), get(), get(), get(), get(), get(), get()) }
    single { ServiceCoordinator(androidContext(), get(), get(), get(), get(named("appScope"))) }

    viewModel { ConnectionViewModel(get(), get(), get(), get(), get(), get(), get()) }
    viewModel { RemoteViewModel(get(), get()) }
    viewModel { LibraryViewModel(get(), get(), get()) }
    viewModel { PlaylistViewModel(get()) }
    viewModel { SettingsViewModel(get()) }
}
