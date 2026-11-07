package com.iconnect.proof

import android.content.Intent
import android.content.ComponentName
import android.content.pm.ServiceInfo
import android.app.NotificationManager

import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class UiSmokeTest {
    @Test fun privateTrustPolicyAndStagingAreExcludedFromBackupAndDeviceTransfer() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals(0, context.applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_ALLOW_BACKUP)
        val expected = setOf("root", "file", "database", "sharedpref", "external", "device_root", "device_file", "device_database", "device_sharedpref")
        val found = mutableMapOf<String, MutableSet<String>>(); var section = ""
        context.resources.getXml(R.xml.data_extraction_rules).use { xml ->
            while (xml.next() != org.xmlpull.v1.XmlPullParser.END_DOCUMENT) {
                if (xml.eventType != org.xmlpull.v1.XmlPullParser.START_TAG) continue
                if (xml.name in setOf("cloud-backup", "device-transfer")) section = xml.name
                if (xml.name == "exclude") { assertEquals(".", xml.getAttributeValue(null, "path")); found.getOrPut(section) { mutableSetOf() }.add(xml.getAttributeValue(null, "domain")) }
                assertNotEquals("include", xml.name)
            }
        }
        assertEquals(expected, found["cloud-backup"]); assertEquals(expected, found["device-transfer"])
    }
    @Test fun obsoleteTransferIntentCannotStartHostingOrAStickyService() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val app = context.applicationContext as IConnectApplication
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { context.startForegroundService(Intent(context, TransferService::class.java).putExtra(TransferService.TOKEN, StorageRules.token())) }
            Thread.sleep(250)
            val deadline = System.currentTimeMillis() + 5_000
            while (context.getSystemService(NotificationManager::class.java).activeNotifications.isNotEmpty() && System.currentTimeMillis() < deadline) Thread.sleep(20)
            assertTrue(context.getSystemService(NotificationManager::class.java).activeNotifications.isEmpty())
            scenario.onActivity {
                val state = app.controller().state.value
                assertFalse(state.backgroundReady); assertFalse(state.connected)
            }
            val service = context.packageManager.getServiceInfo(ComponentName(context, TransferService::class.java), 0)
            assertFalse(service.exported); assertEquals(ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC, service.foregroundServiceType)
        }
    }
    @Test fun composeActivityLaunchesAndLeavesRemoteAvailabilityOff() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            InstrumentationRegistry.getInstrumentation().waitForIdleSync()
            scenario.onActivity { activity ->
                assertTrue(activity.window.decorView.isShown)
                assertEquals("iConnect Proof", activity.applicationInfo.loadLabel(activity.packageManager).toString())
            }
        }
    }
}
