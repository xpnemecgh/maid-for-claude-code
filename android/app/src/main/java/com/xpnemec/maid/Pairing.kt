package com.xpnemec.maid

import android.content.Context
import android.net.Uri

/**
 * What the QR code on the PC carries: maid://pair?v=1&h=<hosts>&p=<port>&k=<certificate pin>&c=<one-time code>&n=<PC name>.
 * The hosts are tried in order (the Tailscale address first, then its name).
 */
data class PairingOffer(val hosts: List<String>, val port: Int, val pin: String, val code: String, val server: String) {
    companion object {
        fun parse(text: String): PairingOffer? {
            val uri = try {
                Uri.parse(text.trim())
            } catch (_: Exception) {
                return null
            }
            if (uri.scheme != "maid" || uri.host != "pair") return null
            val hosts = uri.getQueryParameter("h")?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() && it.length < 256 } ?: return null
            val port = uri.getQueryParameter("p")?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
            val pin = uri.getQueryParameter("k")?.takeIf { it.length in 40..48 } ?: return null
            val code = uri.getQueryParameter("c")?.takeIf { it.length in 16..64 } ?: return null
            if (hosts.isEmpty()) return null
            return PairingOffer(hosts, port, pin, code, uri.getQueryParameter("n")?.take(60) ?: "PC")
        }
    }
}

/** The PC this phone is paired with, and the alias of the phone's key for it. */
data class Paired(val hosts: List<String>, val port: Int, val pin: String, val server: String, val keyAlias: String, val lastHost: String) {
    companion object {
        private const val PREFS = "pairing"

        fun load(context: Context): Paired? {
            val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val hosts = prefs.getString("hosts", null)?.split(',')?.filter { it.isNotEmpty() } ?: return null
            val pin = prefs.getString("pin", null) ?: return null
            val alias = prefs.getString("keyAlias", null) ?: return null
            if (hosts.isEmpty() || !PhoneKey.exists(alias)) return null
            return Paired(hosts, prefs.getInt("port", 0), pin, prefs.getString("server", "PC") ?: "PC", alias, prefs.getString("lastHost", "") ?: "")
        }

        fun save(context: Context, paired: Paired) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString("hosts", paired.hosts.joinToString(","))
                .putInt("port", paired.port)
                .putString("pin", paired.pin)
                .putString("server", paired.server)
                .putString("keyAlias", paired.keyAlias)
                .putString("lastHost", paired.lastHost)
                .apply()
        }

        fun rememberHost(context: Context, host: String) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("lastHost", host).apply()
        }

        fun clear(context: Context) {
            load(context)?.let { PhoneKey.delete(it.keyAlias) }
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().apply()
        }
    }

    /** The hosts in the order to try them: the one that worked last time first. */
    fun hostsToTry(): List<String> = if (lastHost in hosts) listOf(lastHost) + hosts.filter { it != lastHost } else hosts
}
