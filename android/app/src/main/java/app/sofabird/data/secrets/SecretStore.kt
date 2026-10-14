package app.sofabird.data.secrets

import android.content.Context
import android.content.SharedPreferences
import android.util.Log
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import java.util.UUID

/**
 * VLC passwords and companion tokens, keyed by server id, in EncryptedSharedPreferences
 * (AES-256 via Android Keystore). If the Keystore is unusable on a device the store falls back to
 * plain SharedPreferences so the app keeps working; this is logged once.
 */
class SecretStore(context: Context) {
    private val prefs: SharedPreferences = createPrefs(context.applicationContext)

    fun vlcPassword(serverId: String): String? = prefs.getString("vlc_pw_$serverId", null)
    fun setVlcPassword(serverId: String, password: String?) = put("vlc_pw_$serverId", password)

    fun companionToken(serverId: String): String? = prefs.getString("cp_token_$serverId", null)
    fun setCompanionToken(serverId: String, token: String?) = put("cp_token_$serverId", token)

    fun removeAll(serverId: String) {
        prefs.edit().remove("vlc_pw_$serverId").remove("cp_token_$serverId").apply()
    }

    /** Stable id of this phone for the companion `hello.device.id`. */
    fun deviceId(): String {
        prefs.getString("device_id", null)?.let { return it }
        val id = UUID.randomUUID().toString()
        prefs.edit().putString("device_id", id).apply()
        return id
    }

    private fun put(key: String, value: String?) {
        prefs.edit().apply { if (value == null) remove(key) else putString(key, value) }.apply()
    }

    private fun createPrefs(context: Context): SharedPreferences = try {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            "secrets",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    } catch (e: Exception) {
        Log.w(TAG, "EncryptedSharedPreferences unavailable, falling back to plain prefs", e)
        context.getSharedPreferences("secrets_fallback", Context.MODE_PRIVATE)
    }

    private companion object {
        const val TAG = "SecretStore"
    }
}
