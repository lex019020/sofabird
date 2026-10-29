package app.sofabird

import android.app.Application
import app.sofabird.data.player.PlayerController
import app.sofabird.data.servers.CompanionServerSync
import app.sofabird.data.update.AppUpdates
import app.sofabird.di.appModule
import app.sofabird.service.ServiceCoordinator
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import coil3.request.crossfade
import okhttp3.OkHttpClient
import org.koin.android.ext.android.inject
import org.koin.android.ext.koin.androidContext
import org.koin.android.ext.koin.androidLogger
import org.koin.core.context.startKoin

class SofabirdApp : Application(), SingletonImageLoader.Factory {

    private val okHttp: OkHttpClient by inject()
    private val controller: PlayerController by inject()
    private val serviceCoordinator: ServiceCoordinator by inject()
    private val serverSync: CompanionServerSync by inject()
    private val updates: AppUpdates by inject()

    override fun onCreate() {
        super.onCreate()
        startKoin {
            androidLogger()
            androidContext(this@SofabirdApp)
            modules(appModule)
        }
        serviceCoordinator.start()
        serverSync.start()
        controller.autoConnectLast()
        updates.start()
    }

    /** Coil shares the OkHttp client so `/art` requests get the `Authorization` header. */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(OkHttpNetworkFetcherFactory(callFactory = { okHttp })) }
            .crossfade(true)
            .build()
}
