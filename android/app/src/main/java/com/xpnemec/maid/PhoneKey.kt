package com.xpnemec.maid

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import android.util.Base64
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec

/**
 * The phone's identity towards Maid: an EC P-256 key made in the Android keystore (in the StrongBox chip
 * where there is one). Its private half cannot be read out of the phone, not even by this app; Maid knows
 * the public half from the pairing and checks a signature with it at every connection.
 */
object PhoneKey {
    private const val STORE = "AndroidKeyStore"

    /** Makes a new key under [alias] (replacing one of that name). */
    fun create(alias: String) {
        delete(alias)
        try {
            generate(alias, strongBox = true)
        } catch (e: StrongBoxUnavailableException) {
            generate(alias, strongBox = false)
        } catch (e: java.security.ProviderException) {
            // some phones report a missing StrongBox this way
            generate(alias, strongBox = false)
        }
    }

    private fun generate(alias: String, strongBox: Boolean) {
        val spec = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setIsStrongBoxBacked(strongBox)
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, STORE).apply { initialize(spec) }.generateKeyPair()
    }

    fun exists(alias: String): Boolean = store().containsAlias(alias)

    /** The public key as Maid stores it: SubjectPublicKeyInfo (DER), base64. */
    fun publicKey(alias: String): String =
        Base64.encodeToString(store().getCertificate(alias).publicKey.encoded, Base64.NO_WRAP)

    /** SHA256withECDSA over [text] (DER signature, base64). */
    fun sign(alias: String, text: String): String {
        val key = store().getKey(alias, null) as PrivateKey
        val signature = Signature.getInstance("SHA256withECDSA").apply {
            initSign(key)
            update(text.toByteArray(Charsets.UTF_8))
        }.sign()
        return Base64.encodeToString(signature, Base64.NO_WRAP)
    }

    fun delete(alias: String) {
        try {
            store().deleteEntry(alias)
        } catch (_: Exception) {
        }
    }

    private fun store(): KeyStore = KeyStore.getInstance(STORE).apply { load(null) }
}
