package com.xpnemec.maid

import android.content.Context
import android.media.MediaRecorder
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import org.json.JSONObject
import java.io.File
import kotlin.math.log10

/**
 * Dictation for the message box. Records speech (AAC in MP4, mono, 16 kHz: what speech needs, and small to send)
 * into the app's cache while the page shows its level, then sends the recording to Maid (`transcribe`) over the
 * paired connection. Maid has ElevenLabs write it out with the PC's key and answers the page (`transcribed` /
 * `transcribeFailed`): the phone still talks to nobody but Maid, and holds no key.
 *
 * The last recording is kept until the next one starts (or the page is done with it), so that a transcription
 * that failed can be asked for again (`resend`). Everything here runs on the main thread.
 *
 * To the page: `{ native: "dictation", state, ref, … }` with state `recording`, `level` (level 0..1, ms),
 * `transcribing` (reason: "" | "limit" | "background"), `failed` (message, retry?) and `idle`.
 */
class Dictation(private val context: Context, private val toPage: (JSONObject) -> Unit) {
    private val main = Handler(Looper.getMainLooper())
    private var recorder: MediaRecorder? = null
    private var ref = ""
    private var startedAt = 0L
    private val file: File get() = File(context.cacheDir, "dictation.m4a")

    val isRecording: Boolean get() = recorder != null

    private val tick = object : Runnable {
        override fun run() {
            val current = recorder ?: return
            val amplitude = try {
                current.maxAmplitude
            } catch (_: Exception) {
                0
            }
            toPage(event("level").put("level", level(amplitude)).put("ms", SystemClock.elapsedRealtime() - startedAt))
            main.postDelayed(this, LEVEL_MS)
        }
    }

    /** Starts recording for the page's dictation [ref] (the microphone permission is the activity's to get first). */
    fun start(ref: String) {
        release()
        this.ref = ref
        file.delete()
        val created = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(context) else @Suppress("DEPRECATION") MediaRecorder()
        try {
            created.setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
            created.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            created.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
            created.setAudioChannels(1)
            created.setAudioSamplingRate(16_000)
            created.setAudioEncodingBitRate(32_000)
            created.setMaxDuration(MAX_MS)
            created.setOutputFile(file)
            created.setOnInfoListener { _, what, _ ->
                if (what == MediaRecorder.MEDIA_RECORDER_INFO_MAX_DURATION_REACHED) main.post { stop("limit") }
            }
            created.setOnErrorListener { _, what, extra ->
                main.post {
                    release()
                    file.delete()
                    toPage(event("failed").put("message", "The recording broke off (error $what/$extra)."))
                }
            }
            created.prepare()
            created.start()
        } catch (e: Exception) {
            Log.w(TAG, "start", e)
            created.release()
            file.delete()
            toPage(event("failed").put("message", "The microphone could not be used (${e.message ?: e.javaClass.simpleName}). Is another app recording?"))
            return
        }
        recorder = created
        startedAt = SystemClock.elapsedRealtime()
        toPage(event("recording"))
        main.postDelayed(tick, LEVEL_MS)
    }

    /** Ends the recording and sends it to Maid to be written out; [reason] tells the page why, when it did not ask. */
    fun stop(reason: String = "") {
        val current = recorder ?: return
        recorder = null
        main.removeCallbacks(tick)
        val length = SystemClock.elapsedRealtime() - startedAt
        // stop() throws when nothing was recorded yet (stopped right away).
        val complete = try {
            current.stop()
            true
        } catch (_: RuntimeException) {
            false
        }
        current.release()
        if (!complete || length < MIN_MS) {
            file.delete()
            toPage(event("failed").put("message", "Too short: say something before stopping (or hold the microphone while you talk)."))
            return
        }
        send(reason)
    }

    /** Sends the kept recording once more, for the page's new [ref] (the last attempt failed). */
    fun resend(ref: String) {
        if (recorder != null) return
        this.ref = ref
        send("")
    }

    /** Stops without sending anything; the recording is thrown away. */
    fun cancel() {
        release()
        file.delete()
        toPage(event("idle"))
    }

    /** The page has the words: the recording is not needed any more. */
    fun discard() {
        if (recorder == null) file.delete()
    }

    private fun release() {
        main.removeCallbacks(tick)
        val current = recorder ?: return
        recorder = null
        try {
            current.stop()
        } catch (_: RuntimeException) {
        }
        current.release()
    }

    private fun send(reason: String) {
        val (source, mime) = sample() ?: (file to "audio/mp4")
        val bytes = try {
            source.readBytes()
        } catch (_: Exception) {
            null
        }
        if (bytes == null || bytes.isEmpty()) {
            toPage(event("failed").put("message", "There is no recording to send."))
            return
        }
        val message = JSONObject()
            .put("type", "transcribe")
            .put("ref", ref)
            .put("mime", mime)
            .put("audio", Base64.encodeToString(bytes, Base64.NO_WRAP))
        if (!MaidLink.send(message.toString())) {
            toPage(event("failed").put("retry", true).put("message", "Not connected to Maid. The recording is kept: try again once connected."))
            return
        }
        toPage(event("transcribing").put("reason", reason).put("bytes", bytes.size))
    }

    /**
     * Debug builds only: a recording pushed to the app's files folder (`adb push x.m4a
     * /sdcard/Android/data/com.xpnemec.maid/files/dictation-sample.m4a`) goes to Maid instead of the
     * microphone's, so that a test can dictate real words where the emulator hears nothing.
     */
    private fun sample(): Pair<File, String>? {
        if (!BuildConfig.DEBUG) return null
        val sample = context.getExternalFilesDir(null)?.let { File(it, "dictation-sample.m4a") } ?: return null
        return if (sample.isFile) sample to "audio/mp4" else null
    }

    private fun event(state: String) = JSONObject().put("native", "dictation").put("state", state).put("ref", ref)

    companion object {
        private const val TAG = "Dictation"
        private const val LEVEL_MS = 80L
        private const val MIN_MS = 500L
        /** One dictation is at most this long (then it is written out as it is): about 1.2 MB. */
        const val MAX_MS = 5 * 60 * 1000

        /** The recorder's amplitude (0..32767) as 0..1 on a decibel scale, so quiet speech still shows. */
        fun level(amplitude: Int): Double {
            if (amplitude <= 0) return 0.0
            val db = 20 * log10(amplitude / 32767.0)
            return ((db + 50) / 50).coerceIn(0.0, 1.0)
        }
    }
}
