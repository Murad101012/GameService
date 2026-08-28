using System.Diagnostics;
using Microsoft.AspNetCore.Builder;

namespace PhonesBridge.Services;

/// <summary>
/// Launches the small GTK system-tray companion (tray.py) when the server starts
/// and tears it down on shutdown. The tray exposes an "Open Server" action and a
/// "Stop the server" action that SIGTERMs this process. Pure best-effort: any
/// failure simply leaves the server without a tray icon.
/// </summary>
public static class TrayHelper
{
    private static volatile int _currentPid = -1;
    private static readonly object Gate = new();

    /// <summary>Pid of the live tray process, or -1 if none.</summary>
    public static int CurrentPid => _currentPid;

    public static int Spawn(WebApplication app)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return -1;

            string script = Path.Combine(app.Environment.ContentRootPath, "tray.py");
            if (!File.Exists(script))
                return -1;

            string? python = FindPython();
            if (python == null)
                return -1;

            int port = SettingsManager.LoadPortFromDisk();
            string icon = Path.Combine(app.Environment.ContentRootPath, "app-icon.svg");
            InstallThemeIcon(icon);

            string args = $"\"{script}\" --pid {Environment.ProcessId} --port {port} --title PhonesBridge --icon \"{icon}\"";
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            ApplyLinuxSessionEnv(psi);

            using (var proc = Process.Start(psi))
            {
                if (proc == null) return -1;
                lock (Gate) _currentPid = proc.Id;
                return proc.Id;
            }
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Tray] Failed to start system tray icon: {ex.Message}"); */
            return -1;
        }
    }

    /// <summary>
    /// Background loop that checks the tray every few seconds and respawns it if
    /// it ever dies while the server is still running, so the icon always returns.
    /// </summary>
    public static CancellationTokenSource StartMonitor(WebApplication app)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    int pid = _currentPid;
                    bool alive = pid > 0 && IsAlive(pid);
                    if (!alive)
                    {
                        int fresh = Spawn(app);
                        if (fresh > 0) { }
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    /* Console.WriteLine($"[Tray] Monitor error: {ex.Message}"); */
                }
            }
        });
        return cts;
    }

    public static void Stop(int trayPid)
    {
        if (trayPid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(trayPid);
            proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>
    /// Fills in the desktop-session environment (session bus, XDG runtime dir,
    /// display) for child processes when this server was started detached from
    /// the session (systemd, SSH, cron, …) and those variables are missing.
    /// Without them tray.py cannot reach StatusNotifierWatcher and notify-send
    /// cannot reach the notification daemon. Pure best-effort.
    /// </summary>
    internal static void ApplyLinuxSessionEnv(ProcessStartInfo psi)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return;

            // psi.EnvironmentVariables (StringDictionary) THROWS on missing-key reads
            // under net10.0 — always go through TryGetValue on psi.Environment.
            string GetVar(string key)
                => psi.Environment.TryGetValue(key, out var v) ? v : null;
            void SetVarIfMissing(string key, string value)
            {
                var cur = GetVar(key);
                if (string.IsNullOrEmpty(cur))
                    psi.Environment[key] = value;
            }

            // Best source of session env: a live desktop-session process (plasmashell /
            // gnome-shell). Harvest whatever variables we are missing from its environ.
            foreach (string procName in new[] { "plasmashell", "gnome-shell", "cinnamon", "xfce4-session" })
            {
                bool haveAll =
                    !string.IsNullOrEmpty(GetVar("DBUS_SESSION_BUS_ADDRESS")) &&
                    !string.IsNullOrEmpty(GetVar("DISPLAY")) &&
                    !string.IsNullOrEmpty(GetVar("XDG_RUNTIME_DIR"));
                if (haveAll)
                    break;

                try
                {
                    foreach (string pidDir in Directory.GetDirectories("/proc"))
                    {
                        string commPath = Path.Combine(pidDir, "comm");
                        if (!File.Exists(commPath)) continue;
                        string comm;
                        try { comm = File.ReadAllText(commPath).Trim(); } catch { continue; }
                        if (!string.Equals(comm, procName, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            string envFile = Path.Combine(pidDir, "environ");
                            if (!File.Exists(envFile)) continue;
                            foreach (string entry in File.ReadAllText(envFile).Split('\0'))
                            {
                                int eq = entry.IndexOf('=');
                                if (eq <= 0) continue;
                                string k = entry.Substring(0, eq);
                                string v = entry.Substring(eq + 1);
                                if (k.Length == 0 || string.IsNullOrEmpty(v)) continue;
                                SetVarIfMissing(k, v);
                            }
                        }
                        catch { /* permission or raced exit */ }
                        if (string.IsNullOrEmpty(GetVar("DBUS_SESSION_BUS_ADDRESS")))
                            continue;
                        break;
                    }
                }
                catch { }
            }

            // Fallback heuristics when no live session process was found/readable.
            string? runtimeDir = GetVar("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDir))
            {
                int uid = -1;
                try
                {
                    foreach (string line in File.ReadAllLines("/proc/self/status"))
                    {
                        if (line.StartsWith("Uid:", StringComparison.Ordinal))
                        {
                            uid = int.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                            break;
                        }
                    }
                }
                catch { /* keep uid = -1 */ }

                if (uid >= 0 && Directory.Exists($"/run/user/{uid}"))
                    runtimeDir = $"/run/user/{uid}";
            }
            if (!string.IsNullOrEmpty(runtimeDir))
                SetVarIfMissing("XDG_RUNTIME_DIR", runtimeDir);

            if (string.IsNullOrEmpty(GetVar("DBUS_SESSION_BUS_ADDRESS"))
                && !string.IsNullOrEmpty(runtimeDir)
                && File.Exists(Path.Combine(runtimeDir, "bus")))
            {
                SetVarIfMissing("DBUS_SESSION_BUS_ADDRESS", $"unix:path={Path.Combine(runtimeDir, "bus")}");
            }

            // Prefer an X socket owned by this user (the real XWayland of the active
            // session); root-owned ones (login-screen X servers) are stale for us.
            if (string.IsNullOrEmpty(GetVar("DISPLAY")) && Directory.Exists("/tmp/.X11-unix"))
            {
                string? best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string path in Directory.GetFiles("/tmp/.X11-unix"))
                {
                    string name = Path.GetFileName(path);
                    if (name.Length < 2 || name[0] != 'X' || !int.TryParse(name.Substring(1), out _)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.UnixFileMode.HasFlag(System.IO.UnixFileMode.UserWrite)) continue; // not ours
                    }
                    catch { }
                    try
                    {
                        var t = Directory.GetLastWriteTimeUtc(path);
                        if (best == null || t > bestTime) { best = name; bestTime = t; }
                    }
                    catch { }
                }
                if (best != null)
                    SetVarIfMissing("DISPLAY", ":" + best.Substring(1));
            }

            if (string.IsNullOrEmpty(GetVar("WAYLAND_DISPLAY"))
                && !string.IsNullOrEmpty(runtimeDir))
            {
                string? wayland = Directory.GetFiles(runtimeDir, "wayland-*")
                    .Select(Path.GetFileName)
                    .FirstOrDefault(n => n != null && !n.EndsWith(".lock", StringComparison.Ordinal));
                if (wayland != null)
                    SetVarIfMissing("WAYLAND_DISPLAY", wayland);
            }
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[SessionEnv] Could not infer session environment: {ex.Message}"); */
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Installs the app icon into the user's hicolor icon theme so the tray (and
    /// desktop entry) can render it reliably on any panel/StatusNotifierItem host.
    /// </summary>
    private static void InstallThemeIcon(string svgPath)
    {
        try
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home) || !File.Exists(svgPath))
                return;

            string appsDir = Path.Combine(home, ".local", "share", "icons", "hicolor", "scalable", "apps");
            Directory.CreateDirectory(appsDir);
            string dest = Path.Combine(appsDir, "phonesbridge.svg");
            if (!File.Exists(dest) || !File.ReadAllBytes(dest).SequenceEqual(File.ReadAllBytes(svgPath)))
            {
                File.Copy(svgPath, dest, overwrite: true);
                string hicolor = Path.Combine(home, ".local", "share", "icons", "hicolor");
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "gtk-update-icon-cache",
                        Arguments = $"-f -t \"{hicolor}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    proc?.WaitForExit(5000);
                }
                catch { /* cache update is best-effort */ }
            }
        }
        catch
        {
            // icon installation is best-effort
        }
    }

    private static string? FindPython()
    {
        foreach (string candidate in new[] { "python3", "python" })
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = candidate,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (proc == null) continue;
                string? outLine = proc.StandardOutput.ReadLine();
                proc.WaitForExit(2000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(outLine))
                    return outLine.Trim();
            }
            catch
            {
                // try next candidate
            }
        }
        return null;
    }
}