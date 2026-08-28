using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

public class CommunicateSignalR : Hub
{
    private readonly ConnectionManager _connectionManager;
    private readonly DeviceConfigManager _deviceConfigManager;
    private readonly ActionManager _actionManager;
    private readonly TunnelManager _tunnelManager;
    private readonly P2pManager _p2pManager;

    /// <summary>Set by Program.cs at startup for debug log storage.</summary>
    public static ConcurrentQueue<(DateTime Time, string Message)>? DebugLogQueue;

    public CommunicateSignalR(ConnectionManager connectionManager, DeviceConfigManager deviceConfigManager, ActionManager actionManager, TunnelManager tunnelManager, P2pManager p2pManager)
    {
        _connectionManager = connectionManager;
        _deviceConfigManager = deviceConfigManager;
        _actionManager = actionManager;
        _tunnelManager = tunnelManager;
        _p2pManager = p2pManager;
    }

    public void PingFromClient()
    {
        // Quiet ping check
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var device = _connectionManager.GetAllDevices()
            .FirstOrDefault(d => d.ConnectionId == Context.ConnectionId);
        string deviceName = device?.DeviceName ?? string.Empty;

        _connectionManager.RemoveConnectedDeviceByConnectionId(Context.ConnectionId);
        _p2pManager.DeviceDisconnected(deviceName);
        _updaterConnections.TryRemove(Context.ConnectionId, out _);
        _monitorConnections.TryRemove(Context.ConnectionId, out _);

        // 🐛 Device Debug: forget every service this connection had announced so
        // the services re-inform themselves on their next line (2.2 fallback).
        ForgetConnectionServices(Context.ConnectionId);

        // Show a desktop notification when a phone disconnects.
        if (!string.IsNullOrEmpty(deviceName))
            DesktopNotifier.Notify("Device Disconnected", deviceName);

        /* Console.WriteLine($"\n[Device Disconnected]: ConnectionId={Context.ConnectionId} Exception={(exception==null?"null":exception.GetType().Name + ": " + exception.Message)}\n"); */
        if (exception != null) { } // silenced
        return base.OnDisconnectedAsync(exception);
    }

    public async Task ReceiveDeviceRecord(ConnectionIdToPc connectionIdToPc)
    {
        string deviceId = string.IsNullOrWhiteSpace(connectionIdToPc.DeviceId)
            ? Guid.NewGuid().ToString()
            : connectionIdToPc.DeviceId;

        string platform = string.IsNullOrWhiteSpace(connectionIdToPc.Platform) ? "Android" : connectionIdToPc.Platform;
        var identity = new DeviceIdentity(Context.ConnectionId, connectionIdToPc.DeviceName, deviceId, platform);
        _connectionManager.AddNewConnectedDevice(identity);

        // Show a desktop notification when a phone connects.
        DesktopNotifier.Notify("Device Connected", connectionIdToPc.DeviceName);

        // Run any enabled "When new phone connected" actions against this device.
        try
        {
            _actionManager.RunOnConnect(identity);
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[CommunicateSignalR] Auto-action error: {ex.Message}"); */
        }

        // Ensure device has a saved config on PC side (first-time: save global as device config)
        Configuration deviceConfig = _deviceConfigManager.EnsureDeviceHasConfig(deviceId);

        string escapedName = Uri.EscapeDataString(connectionIdToPc.DeviceName);

        // These URLs are always printed and always sent to the phone. Whether the
        // server actually SERVES them is controlled by the Settings toggle (the
        // WebDAV middleware rejects them with 403 when the option is disabled).
        string webdavFull = $"webdavs://phone.sunnygameai.site/communicate/{escapedName}/full/";
        string webdavLowRes = $"webdavs://phone.sunnygameai.site/communicate/{escapedName}/lowres/";

        string httpsFull = $"https://phone.sunnygameai.site/communicate/{escapedName}/full/";
        string httpsLowRes = $"https://phone.sunnygameai.site/communicate/{escapedName}/lowres/";

        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine($"[Device Connected]: {connectionIdToPc.DeviceName} (ID: {deviceId})");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("Dolphin URLs (WebDAV Protocol):");
        Console.WriteLine($"  FULL:   {webdavFull}");
        Console.WriteLine($"  LOWRES: {webdavLowRes}");
        Console.WriteLine();
        Console.WriteLine("HTTPS URLs:");
        Console.WriteLine($"  FULL:   {httpsFull}");
        Console.WriteLine($"  LOWRES: {httpsLowRes}");
        Console.WriteLine("==================================================");
        /* Console.WriteLine(); */

        string endpointUrl = $"FULL: {httpsFull}\nLOWRES: {httpsLowRes}";
        await Clients.Caller.SendAsync("OnEndpointCreated", endpointUrl);

        // Push the saved device config to the newly connected phone
        string configJson = System.Text.Json.JsonSerializer.Serialize(deviceConfig,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await Clients.Caller.SendAsync("RefreshConfiguration", configJson);
        /* Console.WriteLine($"[CommunicateSignalR] Pushed saved config to newly connected device: {connectionIdToPc.DeviceName} (ID: {deviceId})"); */

        // Push direct-link candidates so the phone can attempt a P2P datapath.
        // The phone tries them; if none connects (CGNAT / mobile data) it stays
        // on the Domain relay and the UI tag simply shows P2P unavailable.
        try
        {
            var candidates = _p2pManager.GetCandidateEndpoints();
            await Clients.Caller.SendAsync("P2PStart", candidates);
            /* Console.WriteLine($"[P2P] Sent {candidates.Count} direct-link candidates to {connectionIdToPc.DeviceName}: {string.Join(", ", candidates)}"); */
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[P2P] Failed to send candidates to {connectionIdToPc.DeviceName}: {ex.Message}"); */
        }
    }

    /// <summary>
    /// Receives debug log messages from phone via WebSocket (not HTTP).
    /// </summary>
    public void SendDebug(string message)
    {
        /* Console.WriteLine($"[Debug] {message}"); */
        if (DebugLogQueue != null)
        {
            DebugLogQueue.Enqueue((DateTime.UtcNow, message));
            while (DebugLogQueue.Count > 300) DebugLogQueue.TryDequeue(out _);
        }
    }

    // ── Tunnel transport (phone → PC) ─────────────────────────────────────────

    /// <summary>Bytes the phone read from a tunneled TCP socket.</summary>
    public void TunnelData(int streamId, byte[] data) => _tunnelManager.OnTunnelData(streamId, data);

    /// <summary>Phone reports the tunneled TCP socket opened (ok=true) or failed.</summary>
    public void TunnelStatus(int streamId, bool ok, string message) => _tunnelManager.OnTunnelStatus(streamId, ok, message);

    /// <summary>Phone reports a tunneled TCP socket reached EOF / closed.</summary>
    public void TunnelClosed(int streamId) => _tunnelManager.OnTunnelClosed(streamId);

    /// <summary>A UDP datagram the phone received and is echoing back to the PC.</summary>
    public void TunnelUdpData(int streamId, string host, int port, byte[] data) => _tunnelManager.OnTunnelUdpData(streamId, host, port, data);

    /// <summary>Phone reports the tunneled UDP socket closed.</summary>
    public void TunnelUdpClosed(int streamId) => _tunnelManager.OnTunnelUdpClosed(streamId);

    // ── Speed test (temp) ────────────────────────────────────────────────────

    public static long SpeedTestBytes;
    public static long SpeedTestFrames;
    public static long SpeedTestStartTicks;

    public void SpeedTestData(byte[] data)
    {
        if (data == null || data.Length == 0) return;
        Interlocked.Add(ref SpeedTestBytes, data.Length);
        Interlocked.Increment(ref SpeedTestFrames);
    }

    // ── P2P datapath status ───────────────────────────────────────────────────

    /// <summary>Phone reports its direct P2P socket dropped (so the tag goes grey immediately).</summary>
    public void P2PClosed(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return;
        _p2pManager.DeviceDisconnected(deviceName);
        /* Console.WriteLine($"[P2P] Phone {deviceName} reported its direct link closed."); */
    }

    // ── Update Service Management ──────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, List<BackupInfoDto>> _deviceBackupsMap = new(StringComparer.OrdinalIgnoreCase);

    public static List<BackupInfoDto> GetCachedBackups(string deviceName)
    {
        return _deviceBackupsMap.TryGetValue(deviceName, out var list) ? list : new List<BackupInfoDto>();
    }

    public void ReportBackupList(string deviceName, List<BackupInfoDto> backups)
    {
        if (!string.IsNullOrWhiteSpace(deviceName) && backups != null)
        {
            _deviceBackupsMap[deviceName] = backups;
            /* Console.WriteLine($"[UpdateService] Received {backups.Count} backup records from {deviceName}."); */
        }
    }

    // ── Agent exe inventory (what the Updater can replace on that PC) ─────────

    private static readonly ConcurrentDictionary<string, List<string>> _deviceExeMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Updater reports the .exe files in its GameService folder (itself excluded).</summary>
    public void ReportExeList(string deviceName, List<string> exes)
    {
        if (!string.IsNullOrWhiteSpace(deviceName) && exes != null)
        {
            _deviceExeMap[deviceName] = exes;
            /* Console.WriteLine($"[UpdateService] {deviceName} reports {exes.Count} agent exe(s): {string.Join(", ", exes)}"); */
        }
    }

    public static List<string> GetCachedExeList(string deviceName)
    {
        return _deviceExeMap.TryGetValue(deviceName, out var list) ? list : new List<string>();
    }

    // ── Updater registration (PC Updater owns a connection INDEPENDENT of the
    //    Device List, so the Update tab can always reach it — even when the
    //    Bridge/registered device endpoint is offline) ──────────────────────────

    private static readonly ConcurrentDictionary<string, string> _updaterConnections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PC Updater announces itself on connect so the Update tab lists it
    /// as a target even when the registered device endpoint (Bridge) is down.</summary>
    public void RegisterUpdater(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            _updaterConnections[Context.ConnectionId] = deviceName;
            /* Console.WriteLine($"[UpdateService] Updater registered over its own connection: {deviceName}"); */
        }
    }

    public static List<string> GetUpdaterDevices()
    {
        return _updaterConnections.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Monitor registration (independent like Updater) ───────────────────────

    private static readonly ConcurrentDictionary<string, string> _monitorConnections = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterMonitor(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            _monitorConnections[Context.ConnectionId] = deviceName;
            /* Console.WriteLine($"[MonitorService] Monitor registered: {deviceName}"); */
        }
    }

    public static List<string> GetMonitorDevices()
    {
        return _monitorConnections.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── 🐛 Device Debug: per-device service registry + log buffers ────────────
    // Every log-producing component ("service") announces itself via
    // RegisterDebugSource(device, service) and ships stamped lines through
    // SendServiceDebug. The server forgets a connection's services when that
    // connection drops; services re-inform on their next line.

    public sealed class DebugSourceState
    {
        public string ConnectionId = "";
        public DateTime AnnouncedUtc = DateTime.UtcNow;
    }

    // device(lower) -> service(lower) -> display info
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DebugSourceState>> _deviceServices = new(StringComparer.OrdinalIgnoreCase);
    // connectionId -> set of "device|service" keys owned by this connection (for disconnect cleanup)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connServices = new(StringComparer.OrdinalIgnoreCase);
    // "device|service" -> cached stamped lines
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<(DateTime Time, string Text)>> _svcLogs = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxSvcLogsPerQueue = 400;
    // pending WhoAreYou resolutions: connId|key -> buffered lines awaiting identity
    private static readonly ConcurrentDictionary<string, List<(string Device, string Service, string Line)>> _pendingIdentity = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Server's own name shown in the Debug console device picker.</summary>
    public static string ServerDeviceName { get; set; } = Environment.MachineName;

    private static ConcurrentQueue<(DateTime, string)> LogsQueueFor(string device, string service)
    {
        return _svcLogs.GetOrAdd(NormKey(device, service), _ => new ConcurrentQueue<(DateTime, string)>());
    }

    /// <summary>
    /// Ingest lines harvested from a device's polled debug buffer (phones /
    /// CoreAgent relay). Auto-registers the producing service on first sight —
    /// the buffer response itself is the device announcing its channel.
    /// </summary>
    public static void IngestPolledDebug(string deviceName, string serviceName, IEnumerable<string> lines)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(serviceName)) return;
        var devMap = _deviceServices.GetOrAdd(deviceName.Trim(), _ => new ConcurrentDictionary<string, DebugSourceState>(StringComparer.OrdinalIgnoreCase));
        if (!devMap.ContainsKey(serviceName.Trim()))
            RegisterServiceInternal("polled", deviceName, serviceName);
        AppendSvcLines(deviceName, serviceName, lines);
    }

    private static string NormKey(string device, string service)
        => (device ?? "").Trim().ToLowerInvariant() + "|" + (service ?? "").Trim().ToLowerInvariant();

    private static void AppendSvcLines(string device, string service, IEnumerable<string> lines)
    {
        var q = LogsQueueFor(device, service);
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            q.Enqueue((DateTime.UtcNow, l));
            while (q.Count > MaxSvcLogsPerQueue) q.TryDequeue(out _);
        }
    }

    private static void RegisterServiceInternal(string connectionId, string device, string service)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(service)) return;
        var devMap = _deviceServices.GetOrAdd(device.Trim(), _ => new ConcurrentDictionary<string, DebugSourceState>(StringComparer.OrdinalIgnoreCase));
        var key = NormKey(device, service);
        bool known = devMap.ContainsKey(service.Trim().ToLowerInvariant());
        devMap[service.Trim().ToLowerInvariant()] = new DebugSourceState { ConnectionId = connectionId };
        var connSet = _connServices.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        connSet[key] = 1;

        if (known)
        {
            // Duplicate announce — ignore, but note it in the server console (1.2).
            /* Console.WriteLine($"[DebugSources] {device}/{service} already known — duplicate announce ignored."); */
        }
        else
        {
            /* Console.WriteLine($"[DebugSources] {device} announced service: {service}"); */
            // Flush any lines buffered while identity was unknown.
            if (_pendingIdentity.TryRemove(connectionId + "|" + key, out var buf) && buf != null)
            {
                var lines = buf.Select(b => b.Line).ToList();
                AppendSvcLines(device, service, lines);
                /* Console.WriteLine($"[DebugSources] flushed {lines.Count} buffered line(s) to {device}/{service}"); */
            }
        }
    }

    /// <summary>A service announces itself (server replies OK via completion).</summary>
    public void RegisterDebugSource(string deviceName, string serviceName)
        => RegisterServiceInternal(Context.ConnectionId, deviceName, serviceName);

    /// <summary>Reply to WhoAreYou — identifies the calling connection.</summary>
    public void IAm(string deviceName, string serviceName)
        => RegisterServiceInternal(Context.ConnectionId, deviceName, serviceName);

    /// <summary>
    /// A service ships one or more stamped debug lines. If the server does not
    /// know this service on this connection, it asks "WhoAreYou?" and buffers the
    /// lines; if no answer arrives they land under "Unknown Service" (last resort).
    /// </summary>
    public void SendServiceDebug(string deviceName, string serviceName, List<string> lines)
    {
        try
        {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(serviceName) || lines == null || lines.Count == 0)
            return;
        try
        {
            Configuration? cfg = null;
            if (_connectionManager.TryGetDeviceIdentity(deviceName, out var ident) && ident != null)
                cfg = _deviceConfigManager.LoadDeviceConfig(ident.DeviceId);
            cfg ??= _deviceConfigManager.LoadGlobalConfig();
            if (cfg != null && !cfg.SendDebug) return;
        }
        catch (Exception ex) { /*Console.WriteLine($"[SendServiceDebug] config check failed: {ex.Message}"); */ }

        var devMap = _deviceServices.TryGetValue(deviceName.Trim(), out var dm) ? dm : null;
        bool known = devMap != null && devMap.ContainsKey(serviceName.Trim());

        if (known)
        {
            var state = devMap![serviceName.Trim()];
            if (state.ConnectionId == Context.ConnectionId)
            {
                AppendSvcLines(deviceName, serviceName, lines);
                return;
            }
            // Known but announced over a different (older) connection — treat the
            // caller as authoritative now (it re-informed after reconnect).
            RegisterServiceInternal(Context.ConnectionId, deviceName, serviceName);
            AppendSvcLines(deviceName, serviceName, lines);
            return;
        }

        // Unknown service → WhoAreYou + buffer (fallback chain, 2.2).
        string key = NormKey(deviceName, serviceName);
        string pendKey = Context.ConnectionId + "|" + key;
        var buf = _pendingIdentity.GetOrAdd(pendKey, _ => new List<(string, string, string)>());
        lock (buf)
        {
            foreach (var l in lines)
                if (!string.IsNullOrWhiteSpace(l))
                    buf.Add((deviceName, serviceName, l));
        }
        /* Console.WriteLine($"[DebugSources] unknown service {deviceName}/{serviceName} — asking WhoAreYou ({buf.Count} line(s) buffered)"); */
        try { Clients.Caller.SendAsync("WhoAreYou", deviceName, serviceName); } catch { }

        // Last resort timer: no identity within 5 s → Unknown Service bucket.
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            if (_pendingIdentity.TryRemove(pendKey, out var leftover) && leftover != null && leftover.Count > 0)
            {
                List<(string, string, string)> snap;
                lock (leftover) snap = leftover.ToList();
                AppendSvcLines(deviceName, "Unknown Service", snap.Select(s => s.Item3));
                /* Console.WriteLine($"[DebugSources] {deviceName}/{serviceName} never identified — {snap.Count} line(s) filed under 'Unknown Service'."); */
            }
        });
        } catch (Exception ex) { /*Console.WriteLine($"[SendServiceDebug] FATAL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); */ }
    }

    // ── New per-class log architecture (LogType/ClassName/Log) ──
    public void SendLog(string deviceName, string className, string logType, string log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(className)) return;
            string cls = className.Trim();
            string type = (logType ?? "Message").Trim();
            string msg = log ?? "";

            // Registration via empty log
            if (string.IsNullOrWhiteSpace(msg))
            {
                RegisterServiceInternal(Context.ConnectionId, deviceName, cls);
                /* Console.WriteLine($"[DebugSources] {deviceName} registered class: {cls} (empty log)"); */
                return;
            }

            // Map LogType to stamped prefix
            string prefix = type.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "[ERROR] " :
                            type.Equals("Success", StringComparison.OrdinalIgnoreCase) ? "[SUCCESS] " : "[MESSAGE] ";
            string stamped = prefix + msg;

            // Ensure service is registered (auto-register if this is first log from that class)
            var devMap = _deviceServices.GetOrAdd(deviceName.Trim(), _ => new ConcurrentDictionary<string, DebugSourceState>(StringComparer.OrdinalIgnoreCase));
            if (!devMap.ContainsKey(cls))
                RegisterServiceInternal(Context.ConnectionId, deviceName, cls);

            // Respect SendDebug config
            try
            {
                Configuration? cfg = null;
                if (_connectionManager.TryGetDeviceIdentity(deviceName, out var ident) && ident != null)
                    cfg = _deviceConfigManager.LoadDeviceConfig(ident.DeviceId);
                cfg ??= _deviceConfigManager.LoadGlobalConfig();
                if (cfg != null && !cfg.SendDebug) return;
            }
            catch { }

            AppendSvcLines(deviceName, cls, new[] { stamped });
            /* Console.WriteLine($"[Debug] {deviceName}/{cls} {stamped}"); */
        }
        catch (Exception ex) { /*Console.WriteLine($"[SendLog] {ex.Message}"); */ }
    }

    // Test helper for new per-class log architecture (used by /api/debug/test-log)
    public static void TestSendLog(string deviceName, string className, string logType, string log)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(className)) return;
        string cls = className.Trim();
        string type = (logType ?? "Message").Trim();
        string msg = log ?? "";
        if (string.IsNullOrWhiteSpace(msg))
        {
            RegisterServiceInternal("test", deviceName, cls);
            /* Console.WriteLine($"[DebugSources] {deviceName} registered class: {cls} (test empty log)"); */
            return;
        }
        string prefix = type.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "[ERROR] " :
                        type.Equals("Success", StringComparison.OrdinalIgnoreCase) ? "[SUCCESS] " : "[MESSAGE] ";
        string stamped = prefix + msg;
        var devMap = _deviceServices.GetOrAdd(deviceName.Trim(), _ => new ConcurrentDictionary<string, DebugSourceState>(StringComparer.OrdinalIgnoreCase));
        if (!devMap.ContainsKey(cls))
            RegisterServiceInternal("test", deviceName, cls);
        AppendSvcLines(deviceName, cls, new[] { stamped });
        /* Console.WriteLine($"[Debug] {deviceName}/{cls} {stamped}"); */
    }

    /// <summary>Services currently known for a device (display names as announced).</summary>
    public static List<string> GetDeviceServices(string deviceName)
    {
        var result = new List<string>();
        if (_deviceServices.TryGetValue((deviceName ?? "").Trim(), out var map))
        {
            // Purge legacy single-service bucket so it never reappears
            if (map.ContainsKey("android"))
            {
                map.TryRemove("android", out _);
                var aq = LogsQueueFor(deviceName, "android");
                while (aq.TryDequeue(out _)) { }
                _svcLogs.TryRemove(NormKey(deviceName, "android"), out _);
            }
            foreach (var kv in map) result.Add(kv.Key);
        }
        return result;
    }

    public static List<(DateTime Time, string Text)> GetCachedServiceLogsV2(string deviceName, string serviceName, int limit)
    {
        var q = LogsQueueFor(deviceName, serviceName);
        var all = q.ToList();
        if (limit > 0 && all.Count > limit) all = all.GetRange(all.Count - limit, limit);
        return all;
    }

    public static List<(DateTime Time, string Text, string Service)> GetAllServiceLogsMerged(string deviceName, int limit)
    {
        var merged = new List<(DateTime Time, string Text, string Service)>();
        if (_deviceServices.TryGetValue((deviceName ?? "").Trim(), out var map))
        {
            foreach (var svc in map.Keys)
            {
                if (svc.Equals("android", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var t in LogsQueueFor(deviceName, svc))
                    merged.Add((t.Item1, t.Item2, svc));
            }
        }
        else
        {
            // Fall back to whatever logs exist for this device even if services dropped.
            foreach (var kv in _svcLogs)
            {
                var parts = kv.Key.Split('|');
                if (parts.Length == 2 && parts[0].Equals((deviceName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    if (parts[1].Equals("android", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var t in kv.Value) merged.Add((t.Item1, t.Item2, parts[1]));
                }
            }
        }
        return merged.OrderBy(m => m.Time).TakeLast(limit <= 0 ? 500 : limit).ToList();
    }

    public static void ClearServiceLogs(string deviceName, string serviceName)
    {
        var q = LogsQueueFor(deviceName, serviceName);
        while (q.TryDequeue(out _)) { }
    }

    public static void ClearAllServiceLogs(string deviceName)
    {
        if (_deviceServices.TryGetValue((deviceName ?? "").Trim(), out var map))
        {
            foreach (var svc in map.Keys.ToList())
            {
                var q = LogsQueueFor(deviceName, svc);
                while (q.TryDequeue(out _)) { }
            }
        }
        else
        {
            foreach (var kv in _svcLogs.ToList())
            {
                var parts = kv.Key.Split('|');
                if (parts.Length == 2 && parts[0].Equals((deviceName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    while (kv.Value.TryDequeue(out _)) { }
            }
        }
    }

    private static void ForgetConnectionServices(string connectionId)
    {
        if (!_connServices.TryRemove(connectionId, out var keys) || keys == null) return;
        foreach (var key in keys.Keys)
        {
            var parts = key.Split('|');
            if (parts.Length != 2) continue;
            string device = parts[0], service = parts[1];
            if (_deviceServices.TryGetValue(device, out var map))
            {
                if (map.TryRemove(service, out var st) && st != null && st.ConnectionId == connectionId)
                    /* Console.WriteLine($"[DebugSources] {device}/{service} forgotten (connection closed); waiting for re-inform."); */
                if (map.IsEmpty) _deviceServices.TryRemove(device, out _);
            }
        }
    }

    // ── Remote Shell (GameServiceShell.exe) ───────────────────────────────────

    private static readonly ConcurrentDictionary<string, string> _shellConnections = new(StringComparer.OrdinalIgnoreCase);
    public sealed record ShellCmdResult(string Device, string Command, int ExitCode, string Output, DateTime AtUtc);
    private static readonly ConcurrentDictionary<string, ShellCmdResult> _shellResults = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<ShellCmdResult>> _shellHistory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (string Device, string Command)> _pendingShellCommands = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterShell(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            _shellConnections[Context.ConnectionId] = deviceName;
            /* Console.WriteLine($"[Shell] Shell agent registered: {deviceName}"); */
        }
    }

    public static List<string> GetShellDevices()
    {
        return _shellConnections.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void RememberPendingShell(string requestId, string device, string command)
    {
        _pendingShellCommands[requestId] = (device, command);
    }

    public void ShellCommandResult(string deviceName, string requestId, int exitCode, string output)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        string device = deviceName, command = "";
        if (_pendingShellCommands.TryRemove(requestId.Trim(), out var pend))
        {
            device = pend.Device; command = pend.Command;
        }
        var rec = new ShellCmdResult(device, command, exitCode, output ?? "", DateTime.UtcNow);
        _shellResults[requestId.Trim()] = rec;

        var hq = _shellHistory.GetOrAdd(device.Trim().ToLowerInvariant(), _ => new List<ShellCmdResult>());
        lock (hq)
        {
            hq.Add(rec);
            while (hq.Count > 50) hq.RemoveAt(0);
        }
    }

    public static ShellCmdResult? GetShellResult(string requestId)
        => _shellResults.TryGetValue(requestId.Trim(), out var r) ? r : null;

    public static List<ShellCmdResult>? GetShellHistory(string deviceName)
    {
        return _shellHistory.TryGetValue(deviceName.Trim().ToLowerInvariant(), out var list) ? list : null;
    }

    // ── Snapshot agent registration + operation results ──────────────────────

    private static readonly ConcurrentDictionary<string, string> _snapshotConnections = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<JsonElement>> _snapshotListMap = new(StringComparer.OrdinalIgnoreCase);

    public sealed record SnapshotOpResult(string Op, string Name, bool Ok, string Message, DateTime AtUtc);
    private static readonly ConcurrentDictionary<string, SnapshotOpResult> _snapshotOpResults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Devices whose GameServiceSnapshot.exe link is alive — this channel
    /// works even when every other agent exe is corrupted.</summary>
    public static List<string> GetSnapshotDevices()
    {
        return _snapshotConnections.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void RegisterSnapshot(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            _snapshotConnections[Context.ConnectionId] = deviceName;
            /* Console.WriteLine($"[Snapshot] Snapshot agent registered: {deviceName}"); */
        }
    }

    public void ReportSnapshotList(string deviceName, List<JsonElement> snapshots)
    {
        if (!string.IsNullOrWhiteSpace(deviceName) && snapshots != null)
            _snapshotListMap[deviceName] = snapshots;
    }

    private static void RecordSnapshotOp(string deviceName, string op, string name, bool ok, string message)
    {
        _snapshotOpResults[deviceName] = new SnapshotOpResult(op, name, ok, message ?? "", DateTime.UtcNow);
        /* Console.WriteLine($"[Snapshot] {deviceName} {op} '{name}': {(ok ? "OK" : "FAILED")} {message}"); */
    }

    public void SnapshotTaken(string deviceName, string name, bool ok, string message)
        => RecordSnapshotOp(deviceName, "take", name, ok, message);

    public void SnapshotReverted(string deviceName, string name, bool ok, string message)
        => RecordSnapshotOp(deviceName, "revert", name, ok, message);

    // ── Rollback completion tracking (per-service "Current" badge) ────────────

    public sealed record RollbackResult(string VersionId, string ExeName, bool Ok, DateTime AtUtc);
    private static readonly ConcurrentDictionary<string, RollbackResult> _rollbackResults = new(StringComparer.OrdinalIgnoreCase);

    public void RollbackDone(string deviceName, string versionId, string exeName, bool ok)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return;
        _rollbackResults[deviceName] = new RollbackResult(versionId ?? "", exeName ?? "", ok, DateTime.UtcNow);
        /* Console.WriteLine($"[UpdateService] {deviceName} rollback {(ok ? "completed" : "FAILED")}: {versionId} [{exeName}]"); */
    }

    public static RollbackResult? GetLastRollback(string deviceName)
        => _rollbackResults.TryGetValue(deviceName, out var r) ? r : null;

    public void SnapshotDeleted(string deviceName, string name, bool ok, string renamed, string message)
    {
        RecordSnapshotOp(deviceName, "delete", name, ok, string.IsNullOrWhiteSpace(renamed) ? message : $"{message} | renumbered: {renamed}".Trim(' ', '|'));
    }

    public static List<JsonElement>? GetCachedSnapshots(string deviceName)
        => _snapshotListMap.TryGetValue(deviceName, out var list) ? list : null;

    public static SnapshotOpResult? GetLastSnapshotOp(string deviceName)
        => _snapshotOpResults.TryGetValue(deviceName, out var r) ? r : null;

    public static void ForgetSnapshotOp(string deviceName)
        => _snapshotOpResults.TryRemove(deviceName, out _);

    /// <summary>Union of every device currently reachable through its service links
    /// (Updater / Monitor / LogSender) — independent of the normal Device List.</summary>
    public static List<string> GetServiceConnectedDevices()
    {
        var set = new List<string>();
        foreach (var d in GetUpdaterDevices()) if (!set.Contains(d, StringComparer.OrdinalIgnoreCase)) set.Add(d);
        foreach (var d in GetMonitorDevices()) if (!set.Contains(d, StringComparer.OrdinalIgnoreCase)) set.Add(d);
        foreach (var d in GetSnapshotDevices()) if (!set.Contains(d, StringComparer.OrdinalIgnoreCase)) set.Add(d);
        foreach (var d in GetShellDevices()) if (!set.Contains(d, StringComparer.OrdinalIgnoreCase)) set.Add(d);
        return set;
    }

    // ── Service States (probed by GameServiceMonitorService) ─────────────────

    private static readonly ConcurrentDictionary<string, List<ServiceStateDto>> _deviceServiceStatesMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> _serviceStatesSeenUtc = new(StringComparer.OrdinalIgnoreCase);

    public static List<ServiceStateDto> GetCachedServiceStates(string deviceName)
    {
        return _deviceServiceStatesMap.TryGetValue(deviceName, out var list) ? list : new List<ServiceStateDto>();
    }

    /// <summary>How stale the cached service states for a device are (MaxValue = never reported).</summary>
    public static TimeSpan GetCachedServiceStatesAge(string deviceName)
    {
        if (!_deviceServiceStatesMap.ContainsKey(deviceName)) return TimeSpan.MaxValue;
        return _serviceStatesSeenUtc.TryGetValue(deviceName, out var t) ? DateTime.UtcNow - t : TimeSpan.MaxValue;
    }

    public void ReportServiceStates(string deviceName, List<ServiceStateDto> states)
    {
        if (!string.IsNullOrWhiteSpace(deviceName) && states != null)
        {
            _deviceServiceStatesMap[deviceName] = states;
            _serviceStatesSeenUtc[deviceName] = DateTime.UtcNow;
            /* Console.WriteLine($"[MonitorService] {deviceName} reports {states.Count} service(s): {string.Join(", ", states.Select(s => $"{s.Name}={(s.Status==0?"up":s.Status==1?"unresponsive":"down")}"))}"); */
        }
    }
}