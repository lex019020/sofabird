package dev.vlcremote.app.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaStyleNotificationHelper
import coil3.ImageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.toBitmap
import dev.vlcremote.app.R
import dev.vlcremote.app.data.history.WatchPrompts
import dev.vlcremote.app.data.player.PlayerController
import dev.vlcremote.app.data.settings.SettingsRepository
import dev.vlcremote.app.ui.MainActivity
import dev.vlcremote.app.ui.common.displayNameFromUri
import dev.vlcremote.app.ui.common.formatTime
import dev.vlcremote.protocol.backend.PlayerCommand
import dev.vlcremote.protocol.model.NextEpisode
import dev.vlcremote.protocol.model.PlaybackStatus
import dev.vlcremote.protocol.model.PlayerState
import dev.vlcremote.protocol.model.ResumeChoice
import dev.vlcremote.protocol.model.ResumePrompt
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import org.koin.android.ext.android.inject

/**
 * Foreground service (type `connectedDevice`) that keeps the connection to the PC alive while the
 * app is in the background and shows a media-style notification with transport buttons. Hosts a
 * media3 [MediaSession] backed by [RemotePlayer] so headset buttons and the lock screen work.
 *
 * Deliberately a plain service rather than `MediaSessionService`: that class calls
 * `startForeground` with type `mediaPlayback` itself, which would conflict with the
 * `connectedDevice` declaration we need (nothing plays on the phone).
 */
class PlaybackService : LifecycleService() {

    private val controller: PlayerController by inject()
    private val settings: SettingsRepository by inject()
    private val prompts: WatchPrompts by inject()
    private val imageLoader: ImageLoader by lazy { coil3.SingletonImageLoader.get(this) }

    private var player: RemotePlayer? = null
    private var session: MediaSession? = null
    private var artBitmap: Bitmap? = null
    private var artUrlLoaded: String? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
        val s = settings.settings.value
        val player = RemotePlayer(controller, s.seekShortSec * 1000L, s.seekLongSec * 1000L)
        this.player = player
        session = MediaSession.Builder(this, player)
            .setId("vlc-remote")
            .setSessionActivity(contentIntent())
            .build()

        startForegroundWith(buildNotification(controller.state.value, prompts.resume.value, prompts.next.value))
        controller.serviceRunning.value = true

        lifecycleScope.launch {
            combine(
                combine(controller.state, controller.activeServer) { state, server -> state to (server?.displayName ?: "") },
                prompts.resume,
                prompts.next,
            ) { (state, host), resume, next -> Snapshot(state, host, resume, next) }
                .collectLatest { snap ->
                    player.update(snap.state, snap.host)
                    loadArt(snap.state)
                    notify(buildNotification(snap.state, snap.resume, snap.next))
                }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        when (intent?.action) {
            ACTION_PLAY_PAUSE -> controller.send(PlayerCommand.TogglePause)
            ACTION_SEEK_BACK -> controller.send(PlayerCommand.SeekBy(-settings.settings.value.seekShortSec.toLong()))
            ACTION_SEEK_FORWARD -> controller.send(PlayerCommand.SeekBy(settings.settings.value.seekLongSec.toLong()))
            ACTION_PREV -> controller.send(PlayerCommand.Prev)
            ACTION_NEXT -> controller.send(PlayerCommand.Next)
            ACTION_RESUME -> prompts.answer(ResumeChoice.RESUME)
            ACTION_RESTART -> prompts.answer(ResumeChoice.RESTART)
            ACTION_PLAY_NEXT_EPISODE -> prompts.playNext()
            ACTION_DISMISS_NEXT_EPISODE -> prompts.dismissNext()
            ACTION_STOP_SERVICE -> {
                stopSelf()
                return START_NOT_STICKY
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        controller.serviceRunning.value = false
        session?.release()
        session = null
        player?.release()
        player = null
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    // ------------------------------------------------------------------ notification

    private fun startForegroundWith(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ServiceCompat.startForeground(
                this,
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun notify(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            return // foreground notification still exists; we just cannot update it visibly
        }
        NotificationManagerCompat.from(this).notify(NOTIFICATION_ID, notification)
    }

    private data class Snapshot(val state: PlayerState, val host: String, val resume: ResumePrompt?, val next: NextEpisode?)

    private fun buildNotification(state: PlayerState, resume: ResumePrompt?, next: NextEpisode?): Notification {
        // Questions from the PC replace the media controls while they stand: on Android 13+ a media-style
        // notification shows only the session's own buttons, so custom actions would not be visible there.
        if (resume != null) return buildResumeNotification(resume)
        if (next != null) return buildNextNotification(state, next)
        val host = controller.activeServer.value?.displayName ?: getString(R.string.app_name)
        val playing = state.playback.state == PlaybackStatus.PLAYING
        val title = state.item.title ?: getString(R.string.notif_nothing_playing)
        val subtitle = when (state.playback.state) {
            PlaybackStatus.PLAYING -> getString(R.string.notif_playing_on, host)
            PlaybackStatus.PAUSED -> getString(R.string.notif_paused_on, host)
            PlaybackStatus.STOPPED -> getString(R.string.notif_connected_to, host)
        }
        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(subtitle)
            .setContentIntent(contentIntent())
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setCategory(NotificationCompat.CATEGORY_TRANSPORT)
            .setDeleteIntent(actionIntent(ACTION_STOP_SERVICE))
            .addAction(android.R.drawable.ic_media_previous, getString(R.string.action_prev), actionIntent(ACTION_PREV))
            .addAction(android.R.drawable.ic_media_rew, getString(R.string.action_seek_back), actionIntent(ACTION_SEEK_BACK))
            .addAction(
                if (playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play,
                getString(if (playing) R.string.action_pause else R.string.action_play),
                actionIntent(ACTION_PLAY_PAUSE),
            )
            .addAction(android.R.drawable.ic_media_ff, getString(R.string.action_seek_forward), actionIntent(ACTION_SEEK_FORWARD))
            .addAction(android.R.drawable.ic_media_next, getString(R.string.action_next), actionIntent(ACTION_NEXT))
        artBitmap?.let { builder.setLargeIcon(it) }
        session?.let { s ->
            val style = MediaStyleNotificationHelper.MediaStyle(s)
                .setShowActionsInCompactView(INDEX_SEEK_BACK, INDEX_PLAY_PAUSE, INDEX_SEEK_FORWARD)
            builder.setStyle(style)
        }
        return builder.build()
    }

    /** "Continue from 1:23:45?" with the two answers; VLC waits paused on the PC. */
    private fun buildResumeNotification(resume: ResumePrompt): Notification {
        val title = resume.title?.takeIf { it.isNotBlank() } ?: displayNameFromUri(resume.uri)
        val at = formatTime(resume.position)
        return baseBuilder()
            .setContentTitle(title)
            .setContentText(getString(R.string.notif_resume_text, at))
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .addAction(android.R.drawable.ic_media_play, getString(R.string.resume_continue_from, at), actionIntent(ACTION_RESUME))
            .addAction(android.R.drawable.ic_menu_revert, getString(R.string.resume_from_start), actionIntent(ACTION_RESTART))
            .apply { artBitmap?.let { setLargeIcon(it) } }
            .build()
    }

    /** "Next: <title>" with "Play next" (never automatic) next to play/pause and "Dismiss". */
    private fun buildNextNotification(state: PlayerState, next: NextEpisode): Notification {
        val host = controller.activeServer.value?.displayName ?: getString(R.string.app_name)
        val nextTitle = next.title?.takeIf { it.isNotBlank() } ?: displayNameFromUri(next.uri)
        val playing = state.playback.state == PlaybackStatus.PLAYING
        return baseBuilder()
            .setContentTitle(getString(R.string.notif_next_text, nextTitle))
            .setContentText(state.item.title ?: getString(R.string.notif_connected_to, host))
            .setCategory(NotificationCompat.CATEGORY_TRANSPORT)
            .addAction(android.R.drawable.ic_media_next, getString(R.string.notif_play_next), actionIntent(ACTION_PLAY_NEXT_EPISODE))
            .addAction(
                if (playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play,
                getString(if (playing) R.string.action_pause else R.string.action_play),
                actionIntent(ACTION_PLAY_PAUSE),
            )
            .addAction(
                android.R.drawable.ic_menu_close_clear_cancel,
                getString(R.string.next_episode_dismiss),
                actionIntent(ACTION_DISMISS_NEXT_EPISODE),
            )
            .apply { artBitmap?.let { setLargeIcon(it) } }
            .build()
    }

    private fun baseBuilder(): NotificationCompat.Builder =
        NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(contentIntent())
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDeleteIntent(actionIntent(ACTION_STOP_SERVICE))

    private suspend fun loadArt(state: PlayerState) {
        val url = state.item.artUrl
        if (url == artUrlLoaded) return
        artUrlLoaded = url
        artBitmap = null
        if (url == null) return
        val result = runCatching {
            imageLoader.execute(ImageRequest.Builder(this).data(url).size(ARTWORK_PX).build())
        }.getOrNull()
        if (result is SuccessResult && artUrlLoaded == url) {
            artBitmap = runCatching { result.image.toBitmap() }.getOrNull()
        }
    }

    private fun contentIntent(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun actionIntent(action: String): PendingIntent = PendingIntent.getService(
        this,
        action.hashCode(),
        Intent(this, PlaybackService::class.java).setAction(action),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun createChannel() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        val channel = NotificationChannel(CHANNEL_ID, getString(R.string.notif_channel_name), NotificationManager.IMPORTANCE_LOW).apply {
            description = getString(R.string.notif_channel_description)
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    companion object {
        const val CHANNEL_ID = "playback"

        // Positions of the notification actions shown in the compact view (after "previous").
        private const val INDEX_SEEK_BACK = 1
        private const val INDEX_PLAY_PAUSE = 2
        private const val INDEX_SEEK_FORWARD = 3
        private const val ARTWORK_PX = 512
        const val NOTIFICATION_ID = 1
        const val ACTION_PLAY_PAUSE = "dev.vlcremote.app.PLAY_PAUSE"
        const val ACTION_SEEK_BACK = "dev.vlcremote.app.SEEK_BACK"
        const val ACTION_SEEK_FORWARD = "dev.vlcremote.app.SEEK_FORWARD"
        const val ACTION_PREV = "dev.vlcremote.app.PREV"
        const val ACTION_NEXT = "dev.vlcremote.app.NEXT"
        const val ACTION_STOP_SERVICE = "dev.vlcremote.app.STOP_SERVICE"
        const val ACTION_RESUME = "dev.vlcremote.app.RESUME"
        const val ACTION_RESTART = "dev.vlcremote.app.RESTART"
        const val ACTION_PLAY_NEXT_EPISODE = "dev.vlcremote.app.PLAY_NEXT_EPISODE"
        const val ACTION_DISMISS_NEXT_EPISODE = "dev.vlcremote.app.DISMISS_NEXT_EPISODE"

        fun start(context: Context) {
            val intent = Intent(context, PlaybackService::class.java)
            androidx.core.content.ContextCompat.startForegroundService(context, intent)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, PlaybackService::class.java))
        }
    }
}
