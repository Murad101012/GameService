using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

namespace PhonesBridge.Services;

/// <summary>
/// PC-side application settings — values that are NOT pushed to phones.
/// Persisted in Services/DataContainers/appsettings.json.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// When true, the server serves the Dolphin (WebDAV) and HTTPS device URLs and
    /// prints them on connect. When false (default), those links are rejected (403).
    /// </summary>
    public bool EnableWebDavUrls { get; set; } = false;

    /// <summary>Port the web UI / SignalR server listens on (default 8052).</summary>
    public int Port { get; set; } = 8052;

    /// <summary>
    /// When true, the WebUI binds to 0.0.0.0 and is reachable from other devices on the same LAN (private ranges only).
    /// When false (default), it binds to 127.0.0.1 and is reachable only from this PC.
    /// </summary>
    public bool AllowLanAccess { get; set; } = false;

    // PC Disk Cache settings (which media types to cache)
    public bool CacheImages { get; set; } = true;
    public bool CacheVideos { get; set; } = true;
    public bool CacheAudio { get; set; } = false;
    public bool CacheFiles { get; set; } = false;

    // Size Display Options ("original", "processed", "both")
    public string SizeDisplayMode { get; set; } = "both";

    // Size Display Targets ("image", "video", "both")
    public string SizeDisplayTarget { get; set; } = "both";

    // Offline folder hover popup: whether to show the count of each file type.
    // Default Images + Videos enabled.
    public bool ShowFileCountImage { get; set; } = true;
    public bool ShowFileCountVideo { get; set; } = true;
    public bool ShowFileCountAudio { get; set; } = false;
    public bool ShowFileCountOther { get; set; } = false;

    // PC Thumbnail Cache Limit
    public string CacheLimitMode { get; set; } = "Unlimited"; // "Unlimited" or "Custom"
    public int CacheLimitMb { get; set; } = 1024;
}

public class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine("Services", "DataContainers", "appsettings.json");
    private static readonly string DevConfigPath = "appsettings.Development.json";
    private static readonly string BaseConfigPath = "appsettings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _lock = new();
    private AppSettings _settings;
    private IHost? _host;

    public SettingsManager()
    {
        _settings = Load();
    }

    public AppSettings Current
    {
        get { lock (_lock) return _settings; }
        set { lock (_lock) { _settings = value; Save(); } }
    }

    public bool IsWebDavEnabled
    {
        get { lock (_lock) return _settings.EnableWebDavUrls; }
    }

    public int Port
    {
        get { lock (_lock) return _settings.Port; }
    }

    public bool IsLanAccessEnabled
    {
        get { lock (_lock) return _settings.AllowLanAccess; }
    }

    /// <summary>Attach the running host so a port change can stop and relaunch it.</summary>
    public void AttachHost(IHost host) => _host = host;

    public void SetWebDavEnabled(bool enabled)
    {
        lock (_lock)
        {
            _settings.EnableWebDavUrls = enabled;
            Save();
        }
    }

    public void SetLanAccessEnabled(bool enabled)
    {
        lock (_lock)
        {
            _settings.AllowLanAccess = enabled;
            Save();
        }
    }

    /// <summary>
    /// Persists the new port, syncs the Kestrel config files (where 8052 was
    /// originally written) to the new port, then restarts the app and the browser
    /// lands on the new port.
    /// </summary>
    public void ScheduleRestart(int newPort)
    {
        int port = Math.Clamp(newPort, 1, 65535);
        lock (_lock)
        {
            _settings.Port = port;
            Save();
        }
        SyncKestrelConfig(port);
        Console.WriteLine($"[Settings] Port changed to {port} — restarting the server...");

        _ = Task.Run(async () =>
        {
            await Task.Delay(1800);
            try
            {
                if (_host != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _host.StopAsync(cts.Token);
                }
            }
            catch { }
            try { StartSelf(); }
            catch (Exception ex) { Console.WriteLine($"[Settings] Failed to restart server: {ex.Message}"); }
            Environment.Exit(0);
        });
    }

    public void ScheduleRestart(int newPort, bool allowLanAccess)
    {
        int port = Math.Clamp(newPort, 1, 65535);
        lock (_lock)
        {
            _settings.Port = port;
            _settings.AllowLanAccess = allowLanAccess;
            Save();
        }
        SyncKestrelConfig(port, allowLanAccess);
        Console.WriteLine($"[Settings] Port {port}, LAN {(allowLanAccess ? "enabled" : "disabled")} — restarting the server...");

        _ = Task.Run(async () =>
        {
            await Task.Delay(1800);
            try
            {
                if (_host != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _host.StopAsync(cts.Token);
                }
            }
            catch { }
            try { StartSelf(); }
            catch (Exception ex) { Console.WriteLine($"[Settings] Failed to restart server: {ex.Message}"); }
            Environment.Exit(0);
        });
    }

    private static void StartSelf()
    {
        var args = Environment.GetCommandLineArgs();
        string arg0 = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "PhonesBridge.dll";
        if (Path.GetExtension(arg0).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi);
        }
        else
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = arg0,
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory
            };
            for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
            System.Diagnostics.Process.Start(psi);
        }
    }

    /// <summary>Reads the saved port from appsettings.json (used before the host builds).</summary>
    public static int LoadPortFromDisk()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (s != null && s.Port is >= 1 and <= 65535) return s.Port;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to read port: {ex.Message}");
        }
        return 8052;
    }

    public static bool LoadAllowLanAccessFromDisk()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (s != null) return s.AllowLanAccess;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to read AllowLanAccess: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Writes the given port into the Kestrel endpoint config in appsettings.json
    /// and appsettings.Development.json (where 8052 was originally written).
    /// </summary>
    public static void SyncKestrelConfig(int port)
    {
        SyncKestrelConfig(port, LoadAllowLanAccessFromDisk());
    }

    public static void SyncKestrelConfig(int port, bool allowLanAccess)
    {
        string host = allowLanAccess ? "0.0.0.0" : "127.0.0.1";
        string url = $"http://{host}:{Math.Clamp(port, 1, 65535)}";
        WriteJsonValue(BaseConfigPath, "Kestrel:Endpoints:Http:Url", url);
        WriteJsonValue(DevConfigPath, "Kestrel:Endpoints:Http:Url", url);
        Console.WriteLine($"[Settings] Kestrel listening URL set to {url} (LAN {(allowLanAccess ? "enabled" : "disabled")})");
    }

    public static List<string> GetLocalLanUrls(int port)
    {
        var urls = new List<string>();
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(ip)) continue;
                if (IsPrivateIPv4(ip)) urls.Add($"http://{ip}:{port}");
            }
            if (urls.Count == 0)
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var ip = ua.Address;
                        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (System.Net.IPAddress.IsLoopback(ip)) continue;
                        if (!IsPrivateIPv4(ip)) continue;
                        string url = $"http://{ip}:{port}";
                        if (!urls.Contains(url)) urls.Add(url);
                    }
                }
            }
        }
        catch { }
        return urls.Distinct().ToList();
    }

    public static bool IsPrivateIPv4(System.Net.IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        return false;
    }

    public static bool IsAllowedForLan(System.Net.IPAddress? ip, bool allowLan)
    {
        if (ip == null) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (!allowLan) return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return IsPrivateIPv4(ip);
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            string s = ip.ToString().ToLowerInvariant();
            if (s.StartsWith("fc") || s.StartsWith("fd") || s.StartsWith("fe80")) return true;
        }
        return false;
    }

    private static void WriteJsonValue(string filePath, string jsonPath, string value)
    {
        try
        {
            var node = File.Exists(filePath)
                ? (JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject())
                : new JsonObject();
            var current = node;
            var segs = jsonPath.Split(':');
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (current[segs[i]] is not JsonObject next)
                {
                    next = new JsonObject();
                    current[segs[i]] = next;
                }
                current = next;
            }
            current[segs[^1]] = value;
            File.WriteAllText(filePath, node.ToJsonString(JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to write {jsonPath} in {filePath}: {ex.Message}");
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to load appsettings.json: {ex.Message}");
        }
        return new AppSettings();
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_lock)
            {
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to save appsettings.json: {ex.Message}");
        }
    }
}