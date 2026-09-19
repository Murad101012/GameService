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
                /* Console.WriteLine($"[DesktopNotifier] Notified: {title} — {message}"); */
            }
            else if (OperatingSystem.IsLinux())
            {
                ShowLinuxNotify(title, message);
                /* Console.WriteLine($"[DesktopNotifier] Notified: {title} — {message}"); */
            }
            else
            {
                /* Console.WriteLine($"[DesktopNotifier] Unsupported OS, skipped: {title} — {message}"); */
            }
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[DesktopNotifier] Failed to show notification: {ex.Message}"); */
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

    /// <summary>
    /// Shows a notification with clickable action buttons (Linux notify-send -A).
    /// Returns the id of the clicked button, or null if dismissed/expired/failed.
    /// Each action is (id, label); the id is echoed to stdout by notify-send.
    /// Must be called from a background thread — blocks until the user acts or
    /// the timeout elapses.
    /// </summary>
    public static string? NotifyWithActions(string title, string message,
        IReadOnlyList<(string Id, string Label)> actions, TimeSpan timeout)
    {
        if (!OperatingSystem.IsLinux() || actions.Count == 0)
            return null;
        try
        {
            var psi = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            TrayHelper.ApplyLinuxSessionEnv(psi);
            psi.ArgumentList.Add("--app-name=PhonesBridge");
            psi.ArgumentList.Add("--icon=phone");
            foreach (var (id, label) in actions)
                psi.ArgumentList.Add($"--action={id}={label}");
            psi.ArgumentList.Add("--wait");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);
            using var proc = Process.Start(psi);
            if (proc == null)
                return null;
            // --wait exits on click, dismiss, or server-side expiry; bound it anyway.
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            string choice = (proc.StandardOutput.ReadToEnd() ?? "").Trim();
            return actions.Any(a => a.Id == choice) ? choice : null;
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[DesktopNotifier] Action notify failed: {ex.Message}"); */
            return null;
        }
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