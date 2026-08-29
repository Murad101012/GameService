package com.UnityTechnologies.com.unity.template.urpblank;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

/**
 * Resurrect GameForegroundService after reboot without C#.
 */
public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        String action = intent.getAction();
        if (Intent.ACTION_BOOT_COMPLETED.equals(action) || "android.intent.action.QUICKBOOT_POWERON".equals(action)) {
            try {
                Intent svc = new Intent(context, GameForegroundService.class);
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(svc);
                } else {
                    context.startService(svc);
                }
            } catch (Exception e) {
                SignalRClient.sendClassLog("BootReceiver", "Error", "Boot start failed: " + e.getMessage());
            }
        }
    }
}
