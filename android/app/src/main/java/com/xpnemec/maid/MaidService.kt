package com.xpnemec.maid

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.os.Build
import android.os.IBinder
import org.json.JSONArray
import org.json.JSONObject

/**
 * Keeps the connection to Maid open while the app is in the background, with a notification that says so
 * (and can end it). It never alerts about sessions waiting for the user (by design: no such notifications); the
 * ongoing notification's text only counts them.
 */
class MaidService : Service(), MaidLink.Listener {
    companion object {
        private const val ONGOING_ID = 1
        private const val CHANNEL_CONNECTION = "connection"
        /** The channel of the "is waiting for you" alerts of app 1.2 and older, deleted with what it still shows. */
        private const val OLD_CHANNEL_WAITING = "waiting"
        const val ACTION_DISCONNECT = "com.xpnemec.maid.DISCONNECT"
        const val EXTRA_TILE = "tile"

        /** The app is on the screen. */
        @Volatile var appVisible = false

        fun start(context: Context) {
            context.startForegroundService(Intent(context, MaidService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, MaidService::class.java))
        }

        fun ensureChannels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL_CONNECTION, context.getString(R.string.channel_connection), NotificationManager.IMPORTANCE_MIN).apply {
                description = context.getString(R.string.channel_connection_text)
                setShowBadge(false)
            })
            manager.deleteNotificationChannel(OLD_CHANNEL_WAITING)
        }
    }

    private lateinit var notifications: NotificationManager
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    @Volatile private var summary = ""

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        notifications = getSystemService(NotificationManager::class.java)
        ensureChannels(this)
        MaidLink.init(this)
        if (Build.VERSION.SDK_INT >= 34) startForeground(ONGOING_ID, ongoing(), ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(ONGOING_ID, ongoing())
        MaidLink.addListener(this)
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = MaidLink.networkAvailable().let { }
        }
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(callback)
        networkCallback = callback
        MaidLink.start()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_DISCONNECT) {
            MaidLink.stop()
            stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        MaidLink.removeListener(this)
        networkCallback?.let { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) }
        MaidLink.stop()
        super.onDestroy()
    }

    override fun onLinkStatus(status: JSONObject) = notifications.notify(ONGOING_ID, ongoing())

    override fun onMaidMessage(json: String) {
        val message = try {
            JSONObject(json)
        } catch (_: Exception) {
            return
        }
        when (message.optString("type")) {
            "init", "tiles" -> tiles(message.optJSONArray("tiles") ?: JSONArray())
        }
    }

    /** Keeps the ongoing notification's count of windows up to date (silently). */
    private fun tiles(tiles: JSONArray) {
        var working = 0
        var waiting = 0
        for (i in 0 until tiles.length()) {
            val tile = tiles.optJSONObject(i) ?: continue
            if (tile.optBoolean("busy")) working++
            if (tile.optBoolean("waiting")) waiting++
        }
        val count = tiles.length()
        summary = buildString {
            append("$count window${if (count == 1) "" else "s"}")
            if (working > 0) append(" · $working working")
            if (waiting > 0) append(" · $waiting waiting for you")
        }
        notifications.notify(ONGOING_ID, ongoing())
    }

    private fun ongoing(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val disconnect = PendingIntent.getService(this, 1, Intent(this, MaidService::class.java).setAction(ACTION_DISCONNECT),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val server = MaidLink.server.ifEmpty { "the PC" }
        val text = when (MaidLink.state) {
            MaidLink.State.CONNECTED -> summary.ifEmpty { "Connected" }
            MaidLink.State.CONNECTING, MaidLink.State.PAIRING -> "Connecting…"
            MaidLink.State.OFFLINE -> "Not reachable now; trying again"
            MaidLink.State.DENIED -> "Not accepted; pair the phone again"
            else -> "Not connected"
        }
        return Notification.Builder(this, CHANNEL_CONNECTION)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(getColor(R.color.accent))
            .setContentTitle("Maid on $server")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(false)
            .setVisibility(Notification.VISIBILITY_PRIVATE)
            .addAction(Notification.Action.Builder(null, getString(R.string.disconnect), disconnect).build())
            .build()
    }
}
