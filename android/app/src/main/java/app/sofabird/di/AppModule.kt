package app.sofabird.di

import app.sofabird.data.companion.AndroidCompanionTransports
import app.sofabird.data.companion.BluetoothSupport
import app.sofabird.data.companion.CompanionTransports
import app.sofabird.data.discovery.DiscoveryRepository
import app.sofabird.data.discovery.LocalNetwork
import app.sofabird.data.discovery.NsdDiscovery
import app.sofabird.data.discovery.SubnetScanner
import app.sofabird.data.discovery.UdpBeaconDiscovery
import app.sofabird.data.history.WatchPrompts
import app.sofabird.data.net.ArtAuth
import app.sofabird.data.net.HttpClients
import app.sofabird.data.player.AppVisibility
import app.sofabird.data.player.CompanionLink
import app.sofabird.data.player.CompanionPairingLink
import app.sofabird.data.player.ConnectionTester
import app.sofabird.data.player.DeviceIdentity
import app.sofabird.data.player.PcWaker
import app.sofabird.data.player.PlayerController
import app.sofabird.data.secrets.SecretStore
import app.sofabird.data.servers.CompanionServerSync
import app.sofabird.data.servers.ServerRepository
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.service.ServiceCoordinator
import app.sofabird.ui.connection.ConnectionViewModel
import app.sofabird.ui.history.HistoryViewModel
import app.sofabird.ui.library.LibraryViewModel
import app.sofabird.ui.media.MediaViewModel
import app.sofabird.ui.media.ShowViewModel
import app.sofabird.ui.playlist.PlaylistViewModel
import app.sofabird.ui.remote.RemoteViewModel
import app.sofabird.ui.settings.SettingsViewModel
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
    single { WatchPrompts(get(), get(named("appScope"))) }
    single { PcWaker(get(), get(), get(named("appScope"))) }
    single { ServiceCoordinator(androidContext(), get(), get(), get(), get(named("appScope"))) }

    viewModel { ConnectionViewModel(get(), get(), get(), get(), get(), get(), get()) }
    viewModel { RemoteViewModel(get(), get(), get()) }
    viewModel { LibraryViewModel(get(), get(), get()) }
    viewModel { PlaylistViewModel(get(), get()) }
    viewModel { HistoryViewModel(get(), get()) }
    viewModel { MediaViewModel(get(), get()) }
    viewModel { params -> ShowViewModel(params.get(), get(), get()) }
    viewModel { SettingsViewModel(get()) }
}
