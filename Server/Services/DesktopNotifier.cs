using System;
using System.Diagnostics;
using System.Text;

namespace PhonesBridge.Services;

/// <summary>
/// Shows OS-native desktop notifications. Linux uses notify-send; Windows uses
/// a PowerShell toast (System.Windows.Forms). Failures are logged, never thrown.
/// </summary>
public static class DesktopNotifier
{
    public static void Notify(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ShowWindowsToast(title, message);
                Console.WriteLine($"[DesktopNotifier] Notified: {title} — {message}");
            }
            else if (OperatingSystem.IsLinux())
            {
                ShowLinuxNotify(title, message);
                Console.WriteLine($"[DesktopNotifier] Notified: {title} — {message}");
            }
            else
            {
                Console.WriteLine($"[DesktopNotifier] Unsupported OS, skipped: {title} — {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopNotifier] Failed to show notification: {ex.Message}");
        }
    }

    private static void ShowLinuxNotify(string title, string message)
    {
        var psi = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        TrayHelper.ApplyLinuxSessionEnv(psi);
        psi.ArgumentList.Add("--app-name=PhonesBridge");
        psi.ArgumentList.Add("--icon=phone");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(message);
        Process.Start(psi);
    }

    private static void ShowWindowsToast(string title, string message)
    {
        string script =
            "Add-Type -AssemblyName System.Windows.Forms\r\n" +
            "Add-Type -AssemblyName System.Drawing\r\n" +
            "$n = New-Object System.Windows.Forms.NotifyIcon\r\n" +
            "$n.Icon = [System.Drawing.SystemIcons]::Information\r\n" +
            "$n.Visible = $true\r\n" +
            $"$n.BalloonTipTitle = '{title.Replace("'", "''")}'\r\n" +
            $"$n.BalloonTipText = '{message.Replace("'", "''")}'\r\n" +
            "$n.ShowBalloonTip(3000)\r\n" +
            "Start-Sleep -Seconds 6\r\n" +
            "$n.Dispose()\r\n";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        Process.Start(psi);
    }
}