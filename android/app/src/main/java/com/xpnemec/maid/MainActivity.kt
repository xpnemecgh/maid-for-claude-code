package com.xpnemec.maid

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.KeyguardManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.hardware.biometrics.BiometricManager
import android.hardware.biometrics.BiometricPrompt
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.CancellationSignal
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.view.HapticFeedbackConstants
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowManager
import android.webkit.ConsoleMessage
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import android.window.OnBackInvokedCallback
import android.window.OnBackInvokedDispatcher
import androidx.webkit.JavaScriptReplyProxy
import androidx.webkit.WebMessageCompat
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.google.android.gms.common.moduleinstall.ModuleInstall
import com.google.android.gms.common.moduleinstall.ModuleInstallRequest
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import org.json.JSONObject
import java.io.ByteArrayInputStream

/**
 * The app: the phone UI (web/, bundled in assets/www) in a WebView served from https://appassets.androidplatform.net.
 * The page talks to the app through window.MaidNative (a WebMessageListener, for that origin only): messages with
 * a "type" go to Maid as they are, those with "native" are for the app (scan, pair, unlock, settings…).
 * Messages from Maid and the app's own events come back the same way.
 */
class MainActivity : Activity(), MaidLink.Listener {
    private lateinit var container: FrameLayout
    private lateinit var webView: WebView
    private val main = Handler(Looper.getMainLooper())
    private var reply: JavaScriptReplyProxy? = null
    private var destroyed = false
    private var pendingTile: String? = null
    private var stoppedAt = 0L
    private var locked = false
    private var unlocking = false
    /** A dictation waiting for the microphone permission (its page reference). */
    private var dictationAsked: String? = null
    private val dictation by lazy { Dictation(this) { toPage(it) } }

    private val prefs by lazy { getSharedPreferences("app", MODE_PRIVATE) }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = this
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)
        MaidLink.init(this)
        MaidService.ensureChannels(this)
        pendingTile = intent?.getStringExtra(MaidService.EXTRA_TILE)
        // The app shows what runs on the PC: it locks whenever it starts afresh.
        locked = lockWanted()

        container = FrameLayout(this)
        container.setBackgroundColor(getColor(R.color.panel))
        webView = WebView(this)
        webView.setBackgroundColor(getColor(R.color.bg))
        container.addView(webView, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setContentView(container)
        setUpInsets()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, OnBackInvokedCallback { goBack() })
        }
        applyKeepScreenOn()

        with(webView.settings) {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            textZoom = 100
            setGeolocationEnabled(false)
            setSupportZoom(false)
        }
        val assetLoader = WebViewAssetLoader.Builder()
            .setDomain(HOST)
            .addPathHandler("/", WebViewAssetLoader.AssetsPathHandler(this).let { assets ->
                WebViewAssetLoader.PathHandler { path -> assets.handle("www/" + (path.ifEmpty { "index.html" })) ?: notFound() }
            })
            .build()
        webView.webViewClient = object : WebViewClient() {
            // Only the bundled UI is ever loaded; the page has no business with the network.
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? =
                if (request.url.host == HOST) assetLoader.shouldInterceptRequest(request.url) else notFound()

            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest) = request.url.host != HOST

            override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
                Log.e(TAG, "WebView renderer gone (crash=${detail.didCrash()}), restarting")
                recreate()
                return true
            }
        }
        webView.webChromeClient = object : WebChromeClient() {
            override fun onConsoleMessage(message: ConsoleMessage): Boolean {
                Log.println(if (message.messageLevel() == ConsoleMessage.MessageLevel.ERROR) Log.ERROR else Log.INFO,
                    "MaidWeb", "${message.message()} (${message.sourceId()}:${message.lineNumber()})")
                return true
            }
        }
        if (!WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) {
            Log.e(TAG, "This WebView is too old (no WebMessageListener)")
        } else {
            WebViewCompat.addWebMessageListener(webView, "MaidNative", setOf("https://$HOST")) { _, message: WebMessageCompat, _, isMainFrame, proxy ->
                if (isMainFrame) {
                    reply = proxy
                    message.data?.let { fromPage(it) }
                }
            }
        }
        MaidLink.addListener(this)
        webView.loadUrl("https://$HOST/index.html")
        if (!MaidLink.isPaired) prepareScanner()

        if (Build.VERSION.SDK_INT >= 33 && !prefs.getBoolean("askedNotifications", false)) {
            prefs.edit().putBoolean("askedNotifications", true).apply()
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
    }

    private fun notFound() = WebResourceResponse("text/plain", "utf-8", 404, "Not Found", mapOf(), ByteArrayInputStream(ByteArray(0)))

    /** Edge to edge: the page is kept clear of the status bar, the navigation bar and the keyboard. */
    private fun setUpInsets() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.VANILLA_ICE_CREAM) @Suppress("DEPRECATION") window.setDecorFitsSystemWindows(false)
        window.isNavigationBarContrastEnforced = false
        container.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
            val ime = insets.getInsets(WindowInsets.Type.ime())
            view.setPadding(bars.left, bars.top, bars.right, maxOf(bars.bottom, ime.bottom))
            WindowInsets.CONSUMED
        }
        container.requestApplyInsets()
    }

    override fun onStart() {
        super.onStart()
        MaidService.appVisible = true
        if (stoppedAt > 0 && SystemClock.elapsedRealtime() - stoppedAt > LOCK_AFTER_MS && lockWanted()) locked = true
        if (MaidLink.isPaired) MaidService.start(this)
        toPage(nativeStatus())
        toPage(JSONObject().put("native", "visible").put("visible", true))
    }

    override fun onResume() {
        super.onResume()
        webView.onResume()
        if (locked) unlock()
    }

    override fun onPause() {
        super.onPause()
        webView.onPause()
    }

    override fun onStop() {
        super.onStop()
        // Recording stops out of sight (Android silences the microphone of an app in the background): what was
        // said so far is written out into the message box.
        if (dictation.isRecording) dictation.stop("background")
        MaidService.appVisible = false
        stoppedAt = SystemClock.elapsedRealtime()
        // Out of sight: the session the phone showed goes back to the PC's size.
        MaidLink.send("""{"type":"unview"}""")
        toPage(JSONObject().put("native", "visible").put("visible", false))
        if (!prefs.getBoolean("background", true)) MaidService.stop(this)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        openTile(intent.getStringExtra(MaidService.EXTRA_TILE) ?: return)
    }

    /** Shows one of the PC's windows (from a notification, or Jarvis through [PhoneApi]). */
    fun openTile(tile: String) {
        if (reply != null) toPage(JSONObject().put("native", "openTile").put("id", tile)) else pendingTile = tile
    }

    override fun onDestroy() {
        if (current === this) current = null
        destroyed = true
        if (dictation.isRecording) dictation.cancel()
        MaidLink.removeListener(this)
        (webView.parent as? ViewGroup)?.removeView(webView)
        webView.destroy()
        super.onDestroy()
    }

    // ---- page ↔ app -----------------------------------------------------------------------------------

    private fun toPage(message: JSONObject) = toPage(message.toString())

    private fun toPage(json: String) {
        main.post { if (!destroyed) reply?.postMessage(json) }
    }

    override fun onMaidMessage(json: String) = toPage(json)

    override fun onLinkStatus(status: JSONObject) = toPage(withLock(status))

    private fun nativeStatus() = withLock(MaidLink.status())

    private fun withLock(status: JSONObject) = status.put("locked", locked)

    private fun fromPage(data: String) {
        val message = try {
            JSONObject(data)
        } catch (_: Exception) {
            return
        }
        if (!message.has("native")) {
            // For Maid, unless the app is locked.
            if (!locked) MaidLink.send(data)
            return
        }
        when (message.optString("native")) {
            "ready" -> {
                // A page that (re)loads knows of no dictation.
                dictationAsked = null
                if (dictation.isRecording) dictation.cancel()
                toPage(JSONObject().put("native", "settings").put("settings", settings()))
                toPage(nativeStatus())
                if (!locked) {
                    MaidLink.lastInit?.let { toPage(it) }
                    MaidLink.lastList?.let { toPage(it) }
                    MaidLink.lastTiles?.let { toPage(it) }
                }
                pendingTile?.let { toPage(JSONObject().put("native", "openTile").put("id", it)) }
                pendingTile = null
            }
            "scan" -> scan()
            "pairLink" -> pairWith(message.optString("link"))
            "forget" -> {
                MaidLink.forget()
                MaidService.stop(this)
            }
            "connect" -> {
                MaidService.start(this)
                MaidLink.start()
            }
            "disconnect" -> MaidService.stop(this)
            "unlock" -> unlock()
            "settings" -> saveSettings(message.optJSONObject("settings") ?: JSONObject())
            "copy" -> {
                getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Maid", message.optString("text")))
            }
            "paste" -> {
                val clip = getSystemService(ClipboardManager::class.java).primaryClip
                val text = if (clip != null && clip.itemCount > 0) clip.getItemAt(0).coerceToText(this).toString() else ""
                toPage(JSONObject().put("native", "pasteText").put("text", text))
            }
            "dictate" -> if (!locked) dictate(message)
            "haptic" -> main.post { webView.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
            "viewing" -> MaidLink.viewing = message.optString("id").ifEmpty { null }
            "exit" -> main.post { moveTaskToBack(true) }
        }
    }

    // ---- dictation -------------------------------------------------------------------------------------

    private fun dictate(message: JSONObject) {
        val ref = message.optString("ref")
        when (message.optString("op")) {
            "start" -> {
                if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
                    dictation.start(ref)
                } else {
                    dictationAsked = ref
                    requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MICROPHONE)
                }
            }
            "stop" -> dictation.stop()
            "cancel" -> {
                dictationAsked = null
                dictation.cancel()
            }
            "resend" -> dictation.resend(ref)
            "discard" -> dictation.discard()
            "appSettings" -> startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_MICROPHONE) return
        val ref = dictationAsked ?: return
        dictationAsked = null
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
            dictation.start(ref)
            return
        }
        // Refused for good, Android does not ask any more: only its settings can allow it then.
        val again = shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO)
        toPage(JSONObject().put("native", "dictation").put("state", "failed").put("ref", ref).put("permission", !again)
            .put("message", if (again) "Dictation needs the microphone." else "Dictation needs the microphone: allow it for Maid in Android's settings."))
    }

    // ---- pairing ---------------------------------------------------------------------------------------

    private fun scannerOptions() = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()

    /** Google's scanner comes with Play services on demand: asked for ahead of the first scan. */
    private fun prepareScanner() {
        try {
            ModuleInstall.getClient(this).installModules(
                ModuleInstallRequest.newBuilder().addApi(GmsBarcodeScanning.getClient(this, scannerOptions())).build())
        } catch (e: Exception) {
            Log.w(TAG, "scanner module", e)
        }
    }

    private fun scan() {
        GmsBarcodeScanning.getClient(this, scannerOptions()).startScan()
            .addOnSuccessListener { barcode -> pairWith(barcode.rawValue ?: "") }
            .addOnCanceledListener { toPage(JSONObject().put("native", "pairResult").put("ok", false).put("cancelled", true)) }
            .addOnFailureListener { error ->
                val downloading = error.message?.contains("download", ignoreCase = true) == true
                if (downloading) prepareScanner()
                toPage(JSONObject().put("native", "pairResult").put("ok", false).put("message",
                    if (downloading) "Google Play services is still downloading its code scanner. Try again in a minute, or paste the pairing link instead."
                    else "The code scanner is not available here (${error.message}). Copy the pairing link in Maid and paste it instead."))
            }
    }

    private fun pairWith(link: String) {
        val offer = PairingOffer.parse(link)
        if (offer == null) {
            toPage(JSONObject().put("native", "pairResult").put("ok", false).put("message", "That is not a pairing code of Maid."))
            return
        }
        MaidService.start(this)
        MaidLink.pair(offer) { ok, text ->
            toPage(JSONObject().put("native", "pairResult").put("ok", ok).put("message", text))
            if (!ok && !MaidLink.isPaired) main.post { MaidService.stop(this) }
        }
    }

    // ---- lock ------------------------------------------------------------------------------------------

    /** The app locks only where the phone itself has a screen lock to unlock it with. */
    private fun lockWanted() = MaidLink.isPaired && prefs.getBoolean("appLock", true) && getSystemService(KeyguardManager::class.java).isDeviceSecure

    private fun unlock() {
        if (!locked || unlocking) return
        if (!lockWanted()) {
            setLocked(false)
            return
        }
        unlocking = true
        val builder = BiometricPrompt.Builder(this)
            .setTitle(getString(R.string.unlock_title))
            .setSubtitle(getString(R.string.unlock_subtitle))
        if (Build.VERSION.SDK_INT >= 30) builder.setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL)
        else @Suppress("DEPRECATION") builder.setDeviceCredentialAllowed(true)
        builder.build().authenticate(CancellationSignal(), mainExecutor, object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                unlocking = false
                setLocked(false)
            }

            override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                unlocking = false
                toPage(nativeStatus())
            }
        })
    }

    private fun setLocked(value: Boolean) {
        locked = value
        toPage(nativeStatus())
        if (!value) {
            MaidLink.lastInit?.let { toPage(it) }
            MaidLink.lastList?.let { toPage(it) }
            MaidLink.lastTiles?.let { toPage(it) }
        }
    }

    // ---- settings --------------------------------------------------------------------------------------

    private fun settings() = JSONObject()
        .put("appLock", prefs.getBoolean("appLock", true))
        .put("canLock", getSystemService(KeyguardManager::class.java).isDeviceSecure)
        .put("background", prefs.getBoolean("background", true))
        .put("keepScreenOn", prefs.getBoolean("keepScreenOn", false))
        .put("dictationSend", prefs.getBoolean("dictationSend", false))
        .put("dictationBox", prefs.getBoolean("dictationBox", false))
        .put("version", BuildConfig.VERSION_NAME)

    private fun saveSettings(values: JSONObject) {
        val edit = prefs.edit()
        for (key in listOf("appLock", "background", "keepScreenOn", "dictationSend", "dictationBox")) if (values.has(key)) edit.putBoolean(key, values.optBoolean(key))
        edit.apply()
        main.post { applyKeepScreenOn() }
        toPage(JSONObject().put("native", "settings").put("settings", settings()))
    }

    private fun applyKeepScreenOn() {
        if (prefs.getBoolean("keepScreenOn", false)) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    // ---- back ------------------------------------------------------------------------------------------

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() = goBack() // Android 12 and older

    /** The page closes its sheets and menus first (window.MaidApp.back); otherwise the app goes to the background. */
    private fun goBack() {
        webView.evaluateJavascript("window.MaidApp ? window.MaidApp.back() : false") { result ->
            if (result == "true") return@evaluateJavascript
            moveTaskToBack(true)
        }
    }

    companion object {
        /** The activity while it exists, for [PhoneApi] (show a window). */
        @Volatile var current: MainActivity? = null
            private set
        private const val TAG = "Maid"
        private const val HOST = WebViewAssetLoader.DEFAULT_DOMAIN
        /** Back after this long in the background: locked again. */
        private const val LOCK_AFTER_MS = 60_000L
        private const val REQUEST_MICROPHONE = 2
    }
}
