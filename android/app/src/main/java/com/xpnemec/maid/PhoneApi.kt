package com.xpnemec.maid

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * Maid's API for an app on this phone: the voice assistant app, which [JarvisRelay] lets in by its signing
 * certificate and nobody else. The requests are those of Maid's API on the PC, the ones maidctl sends
 * (`{ "op": …, … }`, see docs/API.md in the Maid repository). They travel over the link this app already keeps
 * to Maid, which answers them as it answers a program on the PC, except that the phone can't change Maid's list.
 * A few requests are this app's own:
 *  - `phone`: whether the app is paired and connected, whether it is on screen, which window it shows
 *  - `show { window }`: the app comes up showing that window (Maid resolves the name, as for any request)
 *  - `open`: the app comes up
 * Every request gets exactly one answer, as JSON text: `{ "ok": true, "result": … }` or `{ "ok": false, "error": "…" }`.
 */
object PhoneApi : MaidLink.Listener {
    private const val CONNECT_WAIT_MS = 12_000L
    private const val ANSWER_WAIT_MS = 30_000L
    /** Maid's own default for ask / wait without a timeout. */
    private const val DEFAULT_WAIT_SECONDS = 600
    private const val MAX_REQUEST_CHARS = 64 * 1024
    private const val SHOW_WAIT_MS = 2_500L

    private val main = Handler(Looper.getMainLooper())
    private val next = AtomicInteger()
    /** Requests Maid hasn't answered yet, by the ref they went with. */
    private val pending = ConcurrentHashMap<String, (JSONObject) -> Unit>()
    private lateinit var app: Context
    @Volatile private var listening = false

    @Synchronized
    private fun init(context: Context) {
        if (listening) return
        app = context.applicationContext
        MaidLink.init(app)
        MaidLink.addListener(this)
        listening = true
    }

    /** Answers [json] (a request) through [done], once, on any thread. */
    fun handle(context: Context, json: String, done: (String) -> Unit) {
        init(context)
        if (json.length > MAX_REQUEST_CHARS) return done(failure("The request is too long."))
        val request = try {
            JSONObject(json)
        } catch (_: Exception) {
            return done(failure("That is not JSON."))
        }
        when (val op = request.optString("op")) {
            "phone" -> done(success(phoneStatus()))
            "open" -> bringUp(null) { shown ->
                done(if (shown) success(JSONObject().put("shown", true)) else failure(notShown(null)))
            }
            "show" -> forward(JSONObject().put("op", "window").put("window", request.optString("window")), ANSWER_WAIT_MS) { answer ->
                val window = answer.optJSONObject("result")
                if (!answer.optBoolean("ok") || window == null) return@forward done(answer.toString())
                val id = window.optString("id")
                bringUp(id) { shown ->
                    done(if (shown) success(JSONObject().put("window", id).put("name", window.optString("name")).put("shown", true))
                    else failure(notShown(id)))
                }
            }
            "" -> done(failure("The request names no \"op\"."))
            else -> {
                // ask / wait hold the answer back until the session's turn is over (or their timeout).
                val seconds = if (request.has("timeout")) request.optInt("timeout", DEFAULT_WAIT_SECONDS) else DEFAULT_WAIT_SECONDS
                val wait = if (op == "ask" || op == "wait") (seconds.coerceIn(0, 3600) + 30) * 1000L else ANSWER_WAIT_MS
                forward(request, wait) { done(it.toString()) }
            }
        }
    }

    private fun success(result: JSONObject) = JSONObject().put("ok", true).put("result", result).toString()

    private fun failure(error: String) = JSONObject().put("ok", false).put("error", error).toString()

    private fun failureObject(error: String) = JSONObject().put("ok", false).put("error", error)

    private fun notShown(tile: String?) =
        "Android didn't let the Maid app come to the front from the background. Start its activity yourself: " +
            "component com.xpnemec.maid/.MainActivity, flag NEW_TASK" + (tile?.let { ", string extra \"${MaidService.EXTRA_TILE}\" = \"$it\"" } ?: "") + "."

    // ---- through Maid ------------------------------------------------------------------------------------------

    /** Sends [request] to Maid; [then] gets Maid's answer ({ ok, result, error }), or a failure when none comes in [waitMs]. */
    private fun forward(request: JSONObject, waitMs: Long, then: (JSONObject) -> Unit) {
        if (!MaidLink.isPaired) return then(failureObject("The Maid app isn't paired with the computer."))
        val ref = "api" + next.incrementAndGet()
        pending[ref] = then
        // A request is a reason to connect, also when the user disconnected the app earlier.
        if (MaidLink.state != MaidLink.State.CONNECTED) MaidLink.start()
        val message = JSONObject().put("type", "api").put("ref", ref).put("request", request).toString()
        sendWhenConnected(ref, message, System.currentTimeMillis() + CONNECT_WAIT_MS)
        main.postDelayed({ pending.remove(ref)?.invoke(failureObject("Maid on the computer didn't answer in time.")) }, CONNECT_WAIT_MS + waitMs)
    }

    private fun sendWhenConnected(ref: String, message: String, deadline: Long) {
        if (!pending.containsKey(ref)) return
        if (MaidLink.state == MaidLink.State.CONNECTED && MaidLink.send(message)) return
        if (System.currentTimeMillis() < deadline) {
            main.postDelayed({ sendWhenConnected(ref, message, deadline) }, 250)
            return
        }
        pending.remove(ref)?.invoke(failureObject("The computer can't be reached: ${MaidLink.state.name.lowercase()}."))
    }

    /** Maid's answers (on the link's thread) go to whoever asked. */
    override fun onMaidMessage(json: String) {
        if (!json.contains("\"apiResult\"")) return
        val message = try {
            JSONObject(json)
        } catch (_: Exception) {
            return
        }
        if (message.optString("type") != "apiResult") return
        val then = pending.remove(message.optString("ref")) ?: return
        val answer = JSONObject().put("ok", message.optBoolean("ok"))
        if (message.optBoolean("ok")) answer.put("result", message.opt("result") ?: JSONObject.NULL)
        else answer.put("error", message.optString("error").ifEmpty { "Maid refused." })
        then(answer)
    }

    override fun onLinkStatus(status: JSONObject) = Unit

    // ---- this app ----------------------------------------------------------------------------------------------

    private fun phoneStatus(): JSONObject {
        val showing = MaidLink.viewing
        return JSONObject()
            .put("app", "Maid")
            .put("version", BuildConfig.VERSION_NAME)
            .put("paired", MaidLink.isPaired)
            .put("link", MaidLink.state.name.lowercase())
            .put("computer", MaidLink.server)
            .put("visible", MaidService.appVisible)
            .put("showing", showing ?: JSONObject.NULL)
            .put("showingName", showing?.let { tileName(it) } ?: JSONObject.NULL)
    }

    /** A window's name as the PC last listed it. */
    private fun tileName(id: String): String? {
        val tiles = try {
            JSONObject(MaidLink.lastTiles ?: return null).optJSONArray("tiles") ?: JSONArray()
        } catch (_: Exception) {
            return null
        }
        for (i in 0 until tiles.length()) {
            val tile = tiles.optJSONObject(i) ?: continue
            if (tile.optString("id") == id) return tile.optString("name")
        }
        return null
    }

    /**
     * Brings the app to the screen (showing [tile], when given); [done] hears whether it is there. Android may refuse
     * an app in the background that: whether it came is seen within a moment.
     */
    private fun bringUp(tile: String?, done: (Boolean) -> Unit) = main.post {
        val activity = MainActivity.current
        if (activity != null && MaidService.appVisible) {
            if (tile != null) activity.openTile(tile)
            done(true)
            return@post
        }
        val intent = Intent(app, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        if (tile != null) intent.putExtra(MaidService.EXTRA_TILE, tile)
        try {
            app.startActivity(intent)
        } catch (_: Exception) {
            done(false)
            return@post
        }
        val deadline = SystemClock.uptimeMillis() + SHOW_WAIT_MS
        val check = object : Runnable {
            override fun run() {
                when {
                    MaidService.appVisible -> done(true)
                    SystemClock.uptimeMillis() < deadline -> main.postDelayed(this, 100)
                    else -> done(false)
                }
            }
        }
        check.run()
    }
}
