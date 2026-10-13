package dev.vlcremote.app

import android.app.Application
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import coil3.request.crossfade
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.servers.CompanionServerSync
import dev.vlcremote.app.di.appModule
import dev.vlcremote.app.service.ServiceCoordinator
import okhttp3.OkHttpClient
import org.koin.android.ext.android.inject
import org.koin.android.ext.koin.androidContext
import org.koin.android.ext.koin.androidLogger
import org.koin.core.context.startKoin

class VlcRemoteApp : Application(), SingletonImageLoader.Factory {

    private val okHttp: OkHttpClient by inject()
    private val controller: PlayerController by inject()
    private val serviceCoordinator: ServiceCoordinator by inject()
    private val serverSync: CompanionServerSync by inject()

    override fun onCreate() {
        super.onCreate()
        startKoin {
            androidLogger()
            androidContext(this@VlcRemoteApp)
            modules(appModule)
        }
        serviceCoordinator.start()
        serverSync.start()
        controller.autoConnectLast()
    }

    /** Coil shares the OkHttp client so `/art` requests get the `Authorization` header. */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(OkHttpNetworkFetcherFactory(callFactory = { okHttp })) }
            .crossfade(true)
            .build()
}
