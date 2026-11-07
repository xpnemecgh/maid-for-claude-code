package com.xpnemec.maid

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Debug builds only: hands a request of Maid's API to [PhoneApi] as Jarvis's would come through [JarvisRelay], so that
 * the phone's side can be tried with adb (the voice assistant app, signed with its own key, may not be on an emulator). The answer
 * goes to logcat (tag MaidApi): `adb shell am broadcast -n com.xpnemec.maid/.DebugApiReceiver --es ref r1 --es request '{"op":"windows"}'`
 */
class DebugApiReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val ref = intent.getStringExtra("ref") ?: "debug"
        val request = intent.getStringExtra("request") ?: return
        Log.i(TAG, "request $ref: $request")
        val pending = goAsync()
        val finished = AtomicBoolean(false)
        PhoneApi.handle(context.applicationContext, request) { result ->
            Log.i(TAG, "result $ref: $result")
            if (finished.compareAndSet(false, true)) pending.finish()
        }
        // A broadcast may hold the app for a few seconds only; a later answer (ask, wait) is still logged.
        Handler(Looper.getMainLooper()).postDelayed({ if (finished.compareAndSet(false, true)) pending.finish() }, 8_000)
    }

    private companion object {
        const val TAG = "MaidApi"
    }
}
