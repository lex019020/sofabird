package app.sofabird.domain.permissions

import android.Manifest
import androidx.annotation.StringRes
import app.sofabird.R

/** What a runtime permission is for; the UI maps it to an icon. */
enum class PermissionKind { NOTIFICATIONS, NEARBY_DEVICES }

/**
 * One runtime permission the app asks for. Project rule: every runtime permission is listed in
 * [PermissionRegistry.all] and requested on the first-run "Permissions" screen with an explanation;
 * in-context requests are only a fallback.
 */
data class PermissionItem(
    val kind: PermissionKind,
    /** `Manifest.permission.*` (must also be declared in AndroidManifest.xml). */
    val permission: String,
    /** First API level where this is a runtime permission; older Android grants it at install. */
    val minSdk: Int,
    /**
     * [PermissionRegistry.screenVersion] in which the item appeared. A new item gets the next number, so
     * users who already passed the screen see it once more after the update (only if not granted yet).
     */
    val addedInScreenVersion: Int,
    @StringRes val title: Int,
    /** Why it is needed — one sentence. */
    @StringRes val rationale: Int,
    /** What does not work without it. */
    @StringRes val without: Int,
)

enum class PermissionStatus {
    GRANTED,

    /** Never asked: the system dialog will appear. */
    NOT_REQUESTED,

    /** Refused once: the system dialog can still appear. */
    DENIED,

    /** Refused for good ("Don't allow" twice / "Don't ask again"): only the app settings can grant it. */
    PERMANENTLY_DENIED,
    ;

    val canRequest: Boolean get() = this == NOT_REQUESTED || this == DENIED
}

/**
 * Status from what Android tells us. `shouldShowRequestPermissionRationale` is `false` both before the
 * first request and after a permanent refusal, so we remember which permissions were already requested.
 */
fun permissionStatus(granted: Boolean, shouldShowRationale: Boolean, requestedBefore: Boolean): PermissionStatus = when {
    granted -> PermissionStatus.GRANTED
    shouldShowRationale -> PermissionStatus.DENIED
    requestedBefore -> PermissionStatus.PERMANENTLY_DENIED
    else -> PermissionStatus.NOT_REQUESTED
}

object PermissionRegistry {

    /** Order = order of the cards and of the system dialogs. */
    val all: List<PermissionItem> = listOf(
        PermissionItem(
            kind = PermissionKind.NOTIFICATIONS,
            permission = Manifest.permission.POST_NOTIFICATIONS,
            minSdk = 33, // Android 13
            addedInScreenVersion = 1,
            title = R.string.perm_notifications_title,
            rationale = R.string.perm_notifications_why,
            without = R.string.perm_notifications_without,
        ),
        PermissionItem(
            kind = PermissionKind.NEARBY_DEVICES,
            permission = Manifest.permission.BLUETOOTH_CONNECT,
            minSdk = 31, // Android 12; before that BLUETOOTH is granted at install
            addedInScreenVersion = 1,
            title = R.string.perm_nearby_title,
            rationale = R.string.perm_nearby_why,
            without = R.string.perm_nearby_without,
        ),
    )

    /** Stored as "seen" once the user passes the screen. */
    val screenVersion: Int = screenVersion(all)

    fun screenVersion(items: List<PermissionItem>): Int = items.maxOfOrNull { it.addedInScreenVersion } ?: 0

    /** Items that are runtime permissions on this Android version. */
    fun applicable(sdkInt: Int, items: List<PermissionItem> = all): List<PermissionItem> =
        items.filter { sdkInt >= it.minSdk }

    /**
     * Items that justify showing the first-run screen: applicable, added after the screen version the
     * user has already seen ([seenVersion] = 0 → never shown) and not granted yet.
     */
    fun pendingForScreen(
        sdkInt: Int,
        seenVersion: Int,
        items: List<PermissionItem> = all,
        isGranted: (String) -> Boolean,
    ): List<PermissionItem> =
        applicable(sdkInt, items).filter { it.addedInScreenVersion > seenVersion && !isGranted(it.permission) }

    fun shouldShowScreen(
        sdkInt: Int,
        seenVersion: Int,
        items: List<PermissionItem> = all,
        isGranted: (String) -> Boolean,
    ): Boolean = pendingForScreen(sdkInt, seenVersion, items, isGranted).isNotEmpty()
}
