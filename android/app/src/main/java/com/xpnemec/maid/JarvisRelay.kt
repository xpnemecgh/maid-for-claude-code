package com.xpnemec.maid

import android.app.Service
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.Message
import android.os.Messenger
import android.os.RemoteException
import android.util.Log
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

/**
 * Lets a voice assistant app on this phone ("Jarvis") hand spoken "on computer, …" requests to Maid on the PC and hear
 * the answers. Only the app named at build time (maid.voiceApp.package), signed with the certificate named with it
 * (maid.voiceApp.certificate), gets through; without them nobody does. The requests travel over the link this app
 * already keeps to Maid: no new way reaches the PC. On the PC they go into the voice tile in All projects (and nowhere
 * else).
 *
 * The assistant also controls Maid with it, by voice: [MSG_API] carries a request of Maid's API (the requests maidctl sends
 * on the PC, plus the phone app's own; see [PhoneApi] and docs/API.md), answered with [MSG_API_RESULT].
 *
 * Protocol (android.os.Messenger, data in the message's Bundle):
 *  - Jarvis → here: [MSG_SEND] { ref, text }, with replyTo set
 *  - here → Jarvis: [MSG_SENT] { ref }, [MSG_REPLY] { ref, text }, [MSG_FAILED] { ref, message }
 *  - Jarvis → here: [MSG_API] { ref, request: JSON text }, with replyTo set
 *  - here → Jarvis: [MSG_API_RESULT] { ref, result: JSON text, { "ok": true, "result": … } or { "ok": false, "error": "…" } }
 */
class JarvisRelay : Service(), MaidLink.Listener {
    companion object {
        private const val TAG = "JarvisRelay"
        const val MSG_SEND = 1
        const val MSG_SENT = 2
        const val MSG_REPLY = 3
        const val MSG_FAILED = 4
        const val MSG_API = 5
        const val MSG_API_RESULT = 6
        /** The voice assistant app's package and the SHA-256 (hex) of its signing certificate: build settings, see build.gradle.kts. */
        private val JARVIS_PACKAGE = BuildConfig.VOICE_APP_PACKAGE
        private val JARVIS_CERTIFICATE = BuildConfig.VOICE_APP_CERTIFICATE
        private const val CONNECT_WAIT_MS = 12_000L
        private const val MAX_TEXT = 2_000
    }

    private val main = Handler(Looper.getMainLooper())
    /** Where the answer to each request goes, by Jarvis's ref. */
    private val clients = ConcurrentHashMap<String, Messenger>()
    private val messenger = Messenger(object : Handler(Looper.getMainLooper()) {
        override fun handleMessage(msg: Message) = received(msg)
    })

    override fun onCreate() {
        super.onCreate()
        MaidLink.init(this)
        MaidLink.addListener(this)
    }

    override fun onDestroy() {
        MaidLink.removeListener(this)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder = messenger.binder

    private fun received(msg: Message) {
        if (msg.what != MSG_SEND && msg.what != MSG_API) return
        if (!fromJarvis(msg.sendingUid)) return
        if (msg.what == MSG_API) return api(msg)
        val client = msg.replyTo ?: return
        val data = msg.data ?: return
        val ref = data.getString("ref").orEmpty().take(64)
        val text = data.getString("text").orEmpty().trim()
        if (ref.isEmpty()) return
        if (text.isEmpty() || text.length > MAX_TEXT) return reply(client, MSG_FAILED, ref, "message" to "The request was empty or too long.")
        if (!MaidLink.isPaired) return reply(client, MSG_FAILED, ref, "message" to "The Maid app isn't paired with the computer.")
        clients[ref] = client
        // A spoken request is a reason to connect, also when the user disconnected the app earlier.
        if (MaidLink.state != MaidLink.State.CONNECTED) MaidLink.start()
        sendWhenConnected(ref, text, System.currentTimeMillis() + CONNECT_WAIT_MS)
    }

    /** A request of Maid's API: [PhoneApi] answers it (through Maid, mostly), and the answer goes back under Jarvis's ref. */
    private fun api(msg: Message) {
        val client = msg.replyTo ?: return
        val data = msg.data ?: return
        val ref = data.getString("ref").orEmpty().take(64)
        if (ref.isEmpty()) return
        PhoneApi.handle(this, data.getString("request").orEmpty()) { result ->
            val bundle = Bundle().apply {
                putString("ref", ref)
                putString("result", result)
            }
            try {
                client.send(Message.obtain(null, MSG_API_RESULT).apply { this.data = bundle })
            } catch (e: RemoteException) {
                Log.w(TAG, "Jarvis is gone: ${e.message}")
            }
        }
    }

    private fun sendWhenConnected(ref: String, text: String, deadline: Long) {
        val client = clients[ref] ?: return
        val message = JSONObject().put("type", "voice").put("ref", ref).put("text", text).toString()
        if (MaidLink.state == MaidLink.State.CONNECTED && MaidLink.send(message)) return
        if (System.currentTimeMillis() < deadline) {
            main.postDelayed({ sendWhenConnected(ref, text, deadline) }, 250)
            return
        }
        clients.remove(ref)
        reply(client, MSG_FAILED, ref, "message" to "The computer can't be reached: ${MaidLink.state.name.lowercase()}.")
    }

    /** Answers from Maid (on the link's thread) go to the Jarvis that asked. */
    override fun onMaidMessage(json: String) {
        val message = try { JSONObject(json) } catch (_: Exception) { return }
        val ref = message.optString("ref")
        when (message.optString("type")) {
            "voiceSent" -> clients[ref]?.let { reply(it, MSG_SENT, ref) }
            "voiceReply" -> clients.remove(ref)?.let { reply(it, MSG_REPLY, ref, "text" to message.optString("text")) }
            "voiceFailed" -> clients.remove(ref)?.let { reply(it, MSG_FAILED, ref, "message" to message.optString("message")) }
        }
    }

    override fun onLinkStatus(status: org.json.JSONObject) = Unit

    private fun reply(client: Messenger, what: Int, ref: String, vararg extras: Pair<String, String>) {
        val data = Bundle().apply {
            putString("ref", ref)
            for ((key, value) in extras) putString(key, value)
        }
        try {
            client.send(Message.obtain(null, what).apply { this.data = data })
        } catch (e: RemoteException) {
            Log.w(TAG, "Jarvis is gone: ${e.message}")
            clients.remove(ref)
        }
    }

    /** The sender is the voice assistant app, signed with its own certificate (a look-alike package name isn't enough). */
    private fun fromJarvis(uid: Int): Boolean {
        if (JARVIS_PACKAGE.isEmpty() || !Regex("[0-9a-f]{64}").matches(JARVIS_CERTIFICATE)) return false
        val packages = packageManager.getPackagesForUid(uid) ?: return false
        if (JARVIS_PACKAGE !in packages) return false
        val certificate = JARVIS_CERTIFICATE.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        return packageManager.hasSigningCertificate(uid, certificate, PackageManager.CERT_INPUT_SHA256)
    }
}
