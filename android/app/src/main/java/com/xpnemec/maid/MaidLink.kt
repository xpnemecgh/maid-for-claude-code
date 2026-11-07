package com.xpnemec.maid

import android.content.Context
import android.os.Handler
import android.os.HandlerThread
import android.provider.Settings
import android.util.Base64
import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.X509TrustManager

/**
 * The connection to Maid on the PC, for the whole app (the page and the service listen to it).
 *
 * TLS to the address from the pairing, trusting exactly Maid's certificate (its fingerprint came with
 * the QR code; no CA, no host name). Maid then says hello with a nonce, and the phone signs
 * "maid-remote/1|auth|<nonce>|<fingerprint>" with its keystore key (or, when pairing, sends its new public
 * key and the one-time code, signed the same way). Everything after that is Maid's JSON messages.
 * Reconnects by itself (the next address, then with growing pauses) until stopped or turned away.
 * All state changes happen on one thread.
 */
object MaidLink {
    private const val TAG = "MaidLink"
    private const val PROTOCOL = "maid-remote/1"
    private val RETRY_SECONDS = longArrayOf(1, 2, 4, 8, 15, 30)

    enum class State { UNPAIRED, STOPPED, CONNECTING, CONNECTED, OFFLINE, DENIED, PAIRING }

    interface Listener {
        /** A message from Maid (after sign-in), as it came. Called on the link's thread. */
        fun onMaidMessage(json: String)
        fun onLinkStatus(status: JSONObject)
    }

    private class Target(val hosts: List<String>, val port: Int, val pin: String, val alias: String, val mode: String, val code: String)

    private lateinit var app: Context
    private val thread = HandlerThread("maid-link").apply { start() }
    private val handler = Handler(thread.looper)
    private val listeners = CopyOnWriteArraySet<Listener>()
    private val clients = HashMap<String, OkHttpClient>()

    @Volatile var state = State.UNPAIRED
        private set
    @Volatile private var detail = ""
    @Volatile var server = ""
        private set
    private var paired: Paired? = null
    private var offer: PairingOffer? = null
    private var offerAlias = ""
    private var offerDone: ((Boolean, String) -> Unit)? = null
    private var running = false
    private var socket: WebSocket? = null
    private var generation = 0
    private var hostQueue = ArrayDeque<String>()
    private var host = ""
    private var signedIn = false
    private var failures = 0
    private val retry = Runnable { connect(fresh = true) }

    /** The window (tile id) the app's page shows, as it last said; for [PhoneApi]'s `phone`. */
    @Volatile var viewing: String? = null

    /** The latest of these, for a page that (re)loads. */
    @Volatile var lastInit: String? = null
        private set
    @Volatile var lastList: String? = null
        private set
    @Volatile var lastTiles: String? = null
        private set

    private var initialized = false

    @Synchronized
    fun init(context: Context) {
        if (initialized) return
        initialized = true
        app = context.applicationContext
        paired = Paired.load(app)
        server = paired?.server ?: ""
        state = if (paired == null) State.UNPAIRED else State.STOPPED
    }

    val isPaired: Boolean get() = paired != null || offer != null

    fun addListener(listener: Listener) = listeners.add(listener)
    fun removeListener(listener: Listener) = listeners.remove(listener)

    /** Connects (and stays connected) when paired. */
    fun start() = handler.post {
        if (paired == null || running) return@post
        running = true
        failures = 0
        connect(fresh = true)
    }

    fun stop() = handler.post {
        running = false
        handler.removeCallbacks(retry)
        drop()
        if (offer == null) setState(if (paired == null) State.UNPAIRED else State.STOPPED, "")
    }

    /** The network changed (Wi-Fi ↔ mobile): try at once instead of waiting for the next retry. */
    fun networkAvailable() = handler.post {
        if (!running || offer != null || state == State.CONNECTED || state == State.CONNECTING) return@post
        handler.removeCallbacks(retry)
        failures = 0
        connect(fresh = true)
    }

    /** Sends a message to Maid; false when not connected. Any thread. */
    fun send(json: String): Boolean {
        val current = socket
        return if (signedIn && current != null) current.send(json) else false
    }

    /** Pairs with the PC of [pairing] using a new key; [done] hears how it went (on the link's thread). */
    fun pair(pairing: PairingOffer, done: (Boolean, String) -> Unit) = handler.post {
        handler.removeCallbacks(retry)
        drop()
        offer?.let { abandonOffer("Replaced by another pairing.") }
        val alias = "maid-key-" + System.currentTimeMillis()
        try {
            PhoneKey.create(alias)
        } catch (e: Exception) {
            Log.e(TAG, "key", e)
            done(false, "The phone could not make a key: ${e.message}")
            return@post
        }
        offer = pairing
        offerAlias = alias
        offerDone = done
        server = pairing.server
        connect(fresh = true)
    }

    /** Forgets the PC: the key goes, and with it any way back in. */
    fun forget() = handler.post {
        running = false
        handler.removeCallbacks(retry)
        drop()
        Paired.clear(app)
        paired = null
        server = ""
        lastInit = null
        lastList = null
        lastTiles = null
        setState(State.UNPAIRED, "")
    }

    fun status(): JSONObject = JSONObject()
        .put("native", "status")
        .put("state", state.name.lowercase())
        .put("detail", detail)
        .put("server", server)
        .put("host", host)
        .put("paired", paired != null)

    // ---- connecting ----------------------------------------------------------------------------------

    private fun target(): Target? {
        offer?.let { return Target(it.hosts, it.port, it.pin, offerAlias, "pair", it.code) }
        paired?.let { return Target(it.hostsToTry(), it.port, it.pin, it.keyAlias, "auth", "") }
        return null
    }

    private fun connect(fresh: Boolean) {
        val target = target() ?: return
        if (fresh || hostQueue.isEmpty()) hostQueue = ArrayDeque(target.hosts)
        host = hostQueue.removeFirst()
        val gen = ++generation
        signedIn = false
        setState(if (offer != null) State.PAIRING else State.CONNECTING, "Connecting to $server ($host)…")
        val url = "https://${if (host.contains(':')) "[$host]" else host}:${target.port}/maid"
        socket = client(target.pin).newWebSocket(Request.Builder().url(url).build(), Events(gen, target))
    }

    private fun drop() {
        generation++
        socket?.cancel()
        socket = null
        signedIn = false
    }

    /** OkHttp's callbacks, moved to the link's thread; those of a connection given up on are dropped. */
    private class Events(private val gen: Int, private val target: Target) : WebSocketListener() {
        override fun onMessage(webSocket: WebSocket, text: String) {
            handler.post { if (gen == generation) received(webSocket, target, text) }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
            handler.post { if (gen == generation) ended(null, reason) }
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            handler.post { if (gen == generation) ended(null, reason) }
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            handler.post { if (gen == generation) ended(t, "") }
        }
    }

    private fun received(webSocket: WebSocket, target: Target, text: String) {
        val type = try {
            JSONObject(text).optString("type")
        } catch (_: Exception) {
            return
        }
        if (!signedIn) {
            val message = JSONObject(text)
            when (type) {
                "hello" -> {
                    server = message.optString("host").ifEmpty { server }
                    val signed = "$PROTOCOL|${target.mode}|${message.optString("nonce")}|${target.pin}"
                    val reply = JSONObject().put("type", target.mode).put("name", deviceName())
                    try {
                        reply.put("sig", PhoneKey.sign(target.alias, signed))
                        if (target.mode == "pair") reply.put("code", target.code).put("key", PhoneKey.publicKey(target.alias))
                    } catch (e: Exception) {
                        Log.e(TAG, "sign", e)
                        turnedAway("key", "The phone's key cannot be used any more (${e.message}). Pair it again.")
                        return
                    }
                    webSocket.send(reply.toString())
                }
                "init" -> {
                    signedIn = true
                    failures = 0
                    signedInAt(target)
                    lastInit = text
                    lastList = null
                    lastTiles = null
                    dispatch(text)
                }
                "denied" -> turnedAway(message.optString("reason"), message.optString("message"))
            }
            return
        }
        when (type) {
            "init" -> lastInit = text
            "list" -> lastList = text
            "tiles" -> lastTiles = text
        }
        dispatch(text)
    }

    private fun signedInAt(target: Target) {
        val pairing = offer
        if (pairing != null) {
            val previous = paired
            val now = Paired(pairing.hosts, pairing.port, pairing.pin, server, offerAlias, host)
            Paired.save(app, now)
            if (previous != null && previous.keyAlias != offerAlias) PhoneKey.delete(previous.keyAlias)
            paired = now
            offer = null
            running = true
            offerDone?.invoke(true, "Paired with $server.")
            offerDone = null
        } else if (paired?.lastHost != host) {
            Paired.rememberHost(app, host)
            paired = paired?.copy(lastHost = host)
        }
        setState(State.CONNECTED, "Connected to $server")
    }

    /** Maid said no (or the key is unusable). Retrying would not help: the phone has to be paired again. */
    private fun turnedAway(reason: String, message: String) {
        drop()
        if (offer != null) {
            abandonOffer(message.ifEmpty { "Maid refused the pairing ($reason)." })
            return
        }
        running = false
        val text = when (reason) {
            "not-paired", "bad-signature", "other-device", "key" -> "Maid does not accept this phone any more. Pair it again (on the PC: Maid → Phone)."
            else -> "Maid refused the connection: ${message.ifEmpty { reason }}"
        }
        setState(State.DENIED, text)
    }

    private fun ended(error: Throwable?, reason: String) {
        socket = null
        val wasIn = signedIn
        signedIn = false
        val wrongCertificate = error is SSLHandshakeException && generateSequence(error as Throwable) { it.cause }.any { it is CertificateException }
        if (offer != null) {
            when {
                wrongCertificate -> abandonOffer("The PC answered with another certificate than the code says. Show a new code in Maid and scan again.")
                hostQueue.isNotEmpty() -> connect(fresh = false)
                else -> abandonOffer("Could not reach $server at ${target()?.hosts?.joinToString()}. Is Tailscale on, on the phone and on the PC, and is the phone app switched on in Maid?")
            }
            return
        }
        if (wrongCertificate) {
            running = false
            setState(State.DENIED, "The PC answered with another certificate than the one paired. If Maid was set up anew, pair the phone again.")
            return
        }
        if (reason.contains("no longer knows", ignoreCase = true)) {
            running = false
            setState(State.DENIED, "Maid forgot this phone. Pair it again (on the PC: Maid → Phone).")
            return
        }
        if (!running) {
            setState(if (paired == null) State.UNPAIRED else State.STOPPED, reason)
            return
        }
        if (!wasIn && hostQueue.isNotEmpty()) {
            connect(fresh = false)
            return
        }
        val delay = RETRY_SECONDS[minOf(failures, RETRY_SECONDS.size - 1)]
        failures++
        val why = when {
            wasIn && reason.isNotEmpty() -> reason
            wasIn -> "The connection to $server was lost."
            else -> "Can't reach $server. Is Tailscale on (on both), and Maid running with the phone app switched on?"
        }
        setState(State.OFFLINE, why)
        handler.postDelayed(retry, delay * 1000)
    }

    private fun abandonOffer(message: String) {
        PhoneKey.delete(offerAlias)
        offer = null
        val done = offerDone
        offerDone = null
        server = paired?.server ?: ""
        setState(if (paired == null) State.UNPAIRED else State.STOPPED, "")
        done?.invoke(false, message)
        // The phone was paired before: back to that.
        if (paired != null && running) connect(fresh = true)
    }

    private fun setState(newState: State, text: String) {
        state = newState
        detail = text
        val status = status()
        for (listener in listeners) listener.onLinkStatus(status)
    }

    private fun dispatch(json: String) {
        for (listener in listeners) listener.onMaidMessage(json)
    }

    private fun deviceName(): String {
        val name = try {
            Settings.Global.getString(app.contentResolver, Settings.Global.DEVICE_NAME)
        } catch (_: Exception) {
            null
        }
        return (name?.takeIf { it.isNotBlank() } ?: "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}").take(60)
    }

    private fun client(pin: String): OkHttpClient = clients.getOrPut(pin) {
        val trust = PinnedTrust(pin)
        val tls = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }
        OkHttpClient.Builder()
            .sslSocketFactory(tls.socketFactory, trust)
            // The certificate is pinned: whatever name or address it is reached by, it is Maid's or nothing.
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(8, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.SECONDS)
            .writeTimeout(20, TimeUnit.SECONDS)
            .pingInterval(15, TimeUnit.SECONDS)
            .retryOnConnectionFailure(false)
            .build()
    }

    /** Trusts one certificate: the one whose public key hashes (SHA-256, base64url) to [pin]. */
    private class PinnedTrust(private val pin: String) : X509TrustManager {
        override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
            val key = chain.firstOrNull()?.publicKey?.encoded ?: throw CertificateException("No certificate")
            val hash = MessageDigest.getInstance("SHA-256").digest(key)
            val presented = Base64.encodeToString(hash, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
            if (!MessageDigest.isEqual(presented.toByteArray(), pin.toByteArray()))
                throw CertificateException("Not the paired PC's certificate")
        }

        override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = throw CertificateException("No clients")

        override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
    }
}
