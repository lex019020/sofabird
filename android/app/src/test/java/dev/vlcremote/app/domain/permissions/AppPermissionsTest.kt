package dev.vlcremote.app.domain.permissions

import android.Manifest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AppPermissionsTest {

    private val notifications = Manifest.permission.POST_NOTIFICATIONS
    private val nearby = Manifest.permission.BLUETOOTH_CONNECT

    private fun permissionsOf(items: List<PermissionItem>) = items.map { it.permission }

    // ------------------------------------------------------------------ registry by SDK level

    @Test
    fun `android 8 to 11 have no runtime permissions to ask`() {
        for (sdk in 26..30) assertEquals("sdk $sdk", emptyList<String>(), permissionsOf(PermissionRegistry.applicable(sdk)))
    }

    @Test
    fun `android 12 asks only for nearby devices`() {
        assertEquals(listOf(nearby), permissionsOf(PermissionRegistry.applicable(31)))
        assertEquals(listOf(nearby), permissionsOf(PermissionRegistry.applicable(32)))
    }

    @Test
    fun `android 13 and newer ask for notifications and nearby devices in registry order`() {
        for (sdk in listOf(33, 34, 35, 36)) {
            assertEquals("sdk $sdk", listOf(notifications, nearby), permissionsOf(PermissionRegistry.applicable(sdk)))
        }
    }

    @Test
    fun `registry entries are unique and versioned`() {
        val all = PermissionRegistry.all
        assertEquals(all.size, all.map { it.permission }.toSet().size)
        assertEquals(all.size, all.map { it.kind }.toSet().size)
        assertTrue(all.all { it.addedInScreenVersion >= 1 })
        assertEquals(all.maxOf { it.addedInScreenVersion }, PermissionRegistry.screenVersion)
    }

    // ------------------------------------------------------------------ when the screen is shown

    @Test
    fun `first launch shows the screen when something is not granted`() {
        assertTrue(PermissionRegistry.shouldShowScreen(sdkInt = 35, seenVersion = 0, isGranted = { false }))
        assertTrue(PermissionRegistry.shouldShowScreen(sdkInt = 35, seenVersion = 0, isGranted = { it == notifications }))
    }

    @Test
    fun `first launch skips the screen when everything is granted or nothing applies`() {
        assertFalse(PermissionRegistry.shouldShowScreen(sdkInt = 35, seenVersion = 0, isGranted = { true }))
        assertFalse(PermissionRegistry.shouldShowScreen(sdkInt = 30, seenVersion = 0, isGranted = { false }))
    }

    @Test
    fun `screen is not shown again after it was passed even if a permission was refused`() {
        val seen = PermissionRegistry.screenVersion
        assertFalse(PermissionRegistry.shouldShowScreen(sdkInt = 35, seenVersion = seen, isGranted = { false }))
    }

    private val v1Notifications = PermissionItem(PermissionKind.NOTIFICATIONS, notifications, 33, 1, 0, 0, 0)
    private val v1Nearby = PermissionItem(PermissionKind.NEARBY_DEVICES, nearby, 31, 1, 0, 0, 0)
    private val v2New = PermissionItem(PermissionKind.NEARBY_DEVICES, "android.permission.NEW_ONE", 35, 2, 0, 0, 0)
    private val v2Registry = listOf(v1Notifications, v1Nearby, v2New)

    @Test
    fun `an update that adds a permission shows the screen once more`() {
        assertEquals(2, PermissionRegistry.screenVersion(v2Registry))
        val pending = PermissionRegistry.pendingForScreen(sdkInt = 35, seenVersion = 1, isGranted = { false }, items = v2Registry)
        // Only the new item justifies it: the old ones were already explained (and maybe refused).
        assertEquals(listOf(v2New), pending)
        assertTrue(PermissionRegistry.shouldShowScreen(35, seenVersion = 1, isGranted = { false }, items = v2Registry))
        // Once passed (version 2 stored) — not again.
        assertFalse(PermissionRegistry.shouldShowScreen(35, seenVersion = 2, isGranted = { false }, items = v2Registry))
    }

    @Test
    fun `new permission that is already granted or not applicable does not bring the screen back`() {
        assertFalse(PermissionRegistry.shouldShowScreen(35, seenVersion = 1, isGranted = { it == v2New.permission }, items = v2Registry))
        assertFalse(PermissionRegistry.shouldShowScreen(34, seenVersion = 1, isGranted = { false }, items = v2Registry))
    }

    @Test
    fun `users upgrading from a version without the screen see it once`() {
        // seenVersion 0: the screen was never shown; Bluetooth not granted → show.
        assertTrue(PermissionRegistry.shouldShowScreen(35, seenVersion = 0, isGranted = { it == notifications }))
    }

    // ------------------------------------------------------------------ status mapping

    @Test
    fun `granted wins over everything`() {
        assertEquals(PermissionStatus.GRANTED, permissionStatus(granted = true, shouldShowRationale = false, requestedBefore = false))
        assertEquals(PermissionStatus.GRANTED, permissionStatus(granted = true, shouldShowRationale = true, requestedBefore = true))
    }

    @Test
    fun `never asked can be requested`() {
        val s = permissionStatus(granted = false, shouldShowRationale = false, requestedBefore = false)
        assertEquals(PermissionStatus.NOT_REQUESTED, s)
        assertTrue(s.canRequest)
    }

    @Test
    fun `refused once can be requested again`() {
        val s = permissionStatus(granted = false, shouldShowRationale = true, requestedBefore = true)
        assertEquals(PermissionStatus.DENIED, s)
        assertTrue(s.canRequest)
        // Rationale flag alone is enough (asked by an older app version that did not record it).
        assertEquals(PermissionStatus.DENIED, permissionStatus(granted = false, shouldShowRationale = true, requestedBefore = false))
    }

    @Test
    fun `asked and no rationale means permanently denied`() {
        val s = permissionStatus(granted = false, shouldShowRationale = false, requestedBefore = true)
        assertEquals(PermissionStatus.PERMANENTLY_DENIED, s)
        assertFalse(s.canRequest)
        assertFalse(PermissionStatus.GRANTED.canRequest)
    }
}
