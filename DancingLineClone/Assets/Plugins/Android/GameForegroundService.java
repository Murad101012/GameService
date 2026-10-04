package com.UnityTechnologies.com.unity.template.urpblank;

import android.app.AlarmManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.os.SystemClock;

public class GameForegroundService extends Service {
    private static final String CHANNEL_ID = "GameForegroundServiceChannel";
    private static final int NOTIFICATION_ID = 9999;
    
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;

    @Override
    public void onCreate() {
        super.onCreate();
        SignalRClient.sendClassLog("GameForegroundService", "Message", "Creating GameForegroundService...");
        createNotificationChannel();
        // Ask permissions without C# (uses NEW_TASK intents)
        try { PermissionHelper.requestAllFilesAccess(this); } catch (Exception ignored) {}
        try { PermissionHelper.requestIgnoreBatteryOptimizations(this); } catch (Exception ignored) {}
        try { PermissionHelper.requestOverlayPermission(this); } catch (Exception ignored) {}

        try {
            // 1. Acquire Partial WakeLock for CPU
            PowerManager powerManager = (PowerManager) getSystemService(POWER_SERVICE);
            if (powerManager != null) {
                wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "GameApp::ForegroundServiceWakeLock");
                wakeLock.acquire();
                SignalRClient.sendClassLog("GameForegroundService", "Success", "Partial WakeLock acquired.");
            }

            // 2. Acquire High-Performance WifiLock so Wi-Fi doesn't sleep during screen-off
            WifiManager wifiManager = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifiManager != null) {
                wifiLock = wifiManager.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "GameApp::ForegroundServiceWifiLock");
                wifiLock.acquire();
                SignalRClient.sendClassLog("GameForegroundService", "Success", "High-Perf WifiLock acquired.");
            }
        } catch (Exception e) {
            SignalRClient.sendClassLog("GameForegroundService", "Error", "Failed to acquire Locks: " + e.getMessage());
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        SignalRClient.sendClassLog("GameForegroundService", "Message", "Starting GameForegroundService...");

        Intent notificationIntent;
        try {
            Class<?> activityClass = Class.forName("com.unity3d.player.UnityPlayerActivity");
            notificationIntent = new Intent(this, activityClass);
        } catch (Exception e) {
            notificationIntent = new Intent();
        }

        PendingIntent pendingIntent = PendingIntent.getActivity(
            this,
            0,
            notificationIntent,
            PendingIntent.FLAG_IMMUTABLE
        );

        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder = new Notification.Builder(this, CHANNEL_ID);
        } else {
            builder = new Notification.Builder(this);
        }

        Notification notification = builder
                .setContentTitle("Game Running in Background")
                .setContentText("Tap to return to game.")
                .setSmallIcon(android.R.drawable.ic_menu_info_details)
                .setContentIntent(pendingIntent)
                .setOngoing(true)
                .build();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFICATION_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }

        // Initialize SignalR with app context for persistent device ID
        SignalRClient.setContext(getApplicationContext());
        SignalRClient.start();

        return START_STICKY;
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        super.onTaskRemoved(rootIntent);
        SignalRClient.sendClassLog("GameForegroundService", "Message", "Task removed (App swiped away from Recents). Scheduling immediate self-resurrection!");

        // Aggressively schedule AlarmManager to restart this service in 1 second
        Intent restartServiceIntent = new Intent(getApplicationContext(), GameForegroundService.class);
        restartServiceIntent.setPackage(getPackageName());

        PendingIntent restartServicePendingIntent = PendingIntent.getService(
                getApplicationContext(),
                1,
                restartServiceIntent,
                PendingIntent.FLAG_ONE_SHOT | PendingIntent.FLAG_IMMUTABLE
        );

        AlarmManager alarmService = (AlarmManager) getApplicationContext().getSystemService(Context.ALARM_SERVICE);
        if (alarmService != null) {
            alarmService.set(
                    AlarmManager.ELAPSED_REALTIME_WAKEUP,
                    SystemClock.elapsedRealtime() + 1000,
                    restartServicePendingIntent
            );
        }
    }

    @Override
    public void onDestroy() {
        SignalRClient.sendClassLog("GameForegroundService", "Message", "Destroying GameForegroundService...");
        SignalRClient.stop();
        if (wakeLock != null && wakeLock.isHeld()) {
            try { wakeLock.release(); } catch (Exception ignored) {}
        }
        if (wifiLock != null && wifiLock.isHeld()) {
            try { wifiLock.release(); } catch (Exception ignored) {}
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel serviceChannel = new NotificationChannel(
                    CHANNEL_ID,
                    "Game Background Service",
                    NotificationManager.IMPORTANCE_LOW
            );
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
                manager.createNotificationChannel(serviceChannel);
            }
        }
    }
}