using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhonesBridge.Services;

/// <summary>
/// Full-capture "TUN mode": routes ALL of the PC's outbound traffic through a
/// connected phone via the SOCKS5 proxy, using the tun2socks binary.
///
/// SAFETY GUARANTEES (this must NEVER take down the host's own internet):
///  * The tun default route is only installed AFTER a real egress test that sends
///    packets through the tun interface to the SOCKS proxy and the phone succeeds.
///  * A watchdog checks every 2s that tun2socks is alive and the tunnel still works;
///    on failure it removes the route, restores DNS and tears the device down, so the
///    host's normal default route comes back automatically (no reboot needed).
///  * Startup sweeps clean up any leftovers from a previous crash.
/// </summary>
public sealed class TunCaptureController
{
    private const string DeviceName = "tun0";
    private const string TunIp = "10.10.0.1";
    private const string TunCidr = "10.10.0.1/24";
    private const int RouteMetric = 10;
    private const string MarkerFile = "/tmp/phonesbridge-tun-active";
    private const string DnsBackupFile = "/tmp/phonesbridge-resolv-backup";
    private const string Tun2SocksLog = "/tmp/phonesbridge-tun2socks.log";
    private const string Tun2SocksPidFile = "/tmp/phonesbridge-tun2socks.pid";
    private const string TestHost = "ip-api.com";

    private readonly Socks5ProxyServer _socks;
    private readonly TunnelManager _tunnels;
    private readonly ConcurrentQueue<string> _log = new();
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdog;
    private int _tun2SocksPid;
    private volatile bool _active;
    private readonly List<string> _bypassRoutes = new();

    public TunCaptureController(Socks5ProxyServer socks, TunnelManager tunnels)
    {
        _socks = socks;
        _tunnels = tunnels;
    }

    public bool IsActive => _active;
    public string Device => DeviceName;
    public string ActiveDevice => _socks.SelectedDevice ?? _tunnels.GetConnectedDevices().FirstOrDefault() ?? "";
    public string[] RecentLog { get { lock (_log) return _log.TakeLast(60).ToArray(); } }

    private void AddLog(string line)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        _log.Enqueue($"[{ts}] {line}");
        while (_log.Count > 200) _log.TryDequeue(out _);
        /* Console.WriteLine($"[Tunnel] {line}"); */
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public async Task<string?> StartAsync()
    {
        if (_active) return "TUN mode is already active.";
        if (!File.Exists(ResolveTun2Socks()))
            return "tun2socks binary not found (expected at tools/tun2socks).";
        if (_tunnels.GetConnectedDevices().Count == 0)
            return "No phone is connected — cannot tunnel.";
        if (!_socks.IsRunning)
            return "SOCKS5 proxy is not running.";

        try
        {
            // 1. TUN device
            await RunSudoAsync("ip", new[] { "tuntap", "add", "dev", DeviceName, "mode", "tun" });
            await RunSudoAsync("ip", new[] { "addr", "replace", TunCidr, "dev", DeviceName });
            await RunSudoAsync("ip", new[] { "link", "set", DeviceName, "up" });
            AddLog($"TUN device {DeviceName} is up ({TunIp}).");

            // 2. Start tun2socks → SOCKS5 → phone
            if (!StartTun2Socks())
            {
                await RollbackAsync();
                return "Failed to start tun2socks.";
            }
            AddLog($"tun2socks started (pid {_tun2SocksPid}).");

            // 3. Prove the whole path works BEFORE touching routing: send a real
            //    request out through tun0 → tun2socks → SOCKS → phone.
            string? egress = await TestThroughTunAsync(TimeSpan.FromSeconds(20));
            if (egress == null)
            {
                AddLog("Pre-flight egress test through TUN FAILED — rolling back without touching routing.");
                await RollbackAsync();
                return "Pre-flight test failed (tunnel not relaying yet). No routes were changed.";
            }
            AddLog($"Pre-flight egress test OK — destinations would see {egress}.");

            // 4. Keep the tunnel's own control path OUT of the tunnel. The phone
            //    reaches this PC through cloudflared (Cloudflare). If those control
            //    connections get routed into tun0 the relay loops and the phone
            //    drops instantly. Pin Cloudflare's ranges to the real gateway.
            await InstallBypassRoutesAsync();

            // 5. NOW install the tun default route + swap DNS for no-leak.
            await RunSudoAsync("ip", new[] { "route", "replace", "default", "dev", DeviceName, "metric", RouteMetric.ToString() });
            AddLog("Default route now via tun0 (metric 10); Wi-Fi/LAN routes untouched.");

            string? dnsError = SwapDnsForTunnel();
            if (dnsError != null) AddLog($"DNS swap: {dnsError}");

            await File.WriteAllTextAsync(MarkerFile, $"{_tun2SocksPid}\n");
            _active = true;

            _watchdogCts = new CancellationTokenSource();
            _watchdog = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token));
            AddLog("TUN full-capture ACTIVE — watchdog engaged.");
            return null;
        }
        catch (Exception ex)
        {
            AddLog($"TUN start failed: {ex.Message}");
            await RollbackAsync();
            return $"Failed to start TUN mode: {ex.Message}";
        }
    }

    public async Task StopAsync()
    {
        if (!_active && _tun2SocksPid == 0) return;
        AddLog("Stopping TUN mode…");
        await RollbackAsync();
        AddLog("TUN mode stopped. Host internet restored.");
    }

    private async Task RollbackAsync()
    {
        _watchdogCts?.Cancel();
        try { if (_watchdog != null) await Task.WhenAny(_watchdog, Task.Delay(2000)); } catch { }

        await RunSudoAsync("ip", new[] { "route", "del", "default", "dev", DeviceName, "metric", RouteMetric.ToString() });
        await RemoveBypassRoutesAsync();
        RestoreDns();

        if (_tun2SocksPid > 0)
        {
            try
            {
                var psi = new ProcessStartInfo("sudo") { ArgumentList = { "-n", "kill", _tun2SocksPid.ToString() }, UseShellExecute = false };
                using var kp = Process.Start(psi);
                kp?.WaitForExit(3000);
            }
            catch { }
            _tun2SocksPid = 0;
        }
        await RunSudoAsync("ip", new[] { "link", "set", DeviceName, "down" });
        await RunSudoAsync("ip", new[] { "tuntap", "del", "dev", DeviceName, "mode", "tun" });

        try { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); } catch { }
        _active = false;
    }

    // ── tun2socks process ──────────────────────────────────────────────────

    private bool StartTun2Socks()
    {
        try
        {
            string bin = ResolveTun2Socks();
            string cmd = $"exec {bin} --device {DeviceName} --proxy socks5://127.0.0.1:{_socks.BoundPort} >> {Tun2SocksLog} 2>&1 & echo $! > {Tun2SocksPidFile}";
            var psi = new ProcessStartInfo("sudo")
            {
                ArgumentList = { "-n", "sh", "-c", cmd },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5000);
            string pidStr = File.Exists(Tun2SocksPidFile) ? File.ReadAllText(Tun2SocksPidFile).Trim() : "";
            if (!int.TryParse(pidStr, out _tun2SocksPid) || _tun2SocksPid <= 0) return false;
            return IsProcessAlive(_tun2SocksPid);
        }
        catch (Exception ex)
        {
            AddLog($"StartTun2Socks error: {ex.Message}");
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        return pid > 0 && Directory.Exists($"/proc/{pid}");
    }

    private string ResolveTun2Socks()
    {
        string[] candidates =
        {
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "tun2socks"),
            Path.Combine(AppContext.BaseDirectory, "tools", "tun2socks"),
            Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory) ?? "", "tools", "tun2socks")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    // ── Pre-flight + watchdog egress test through the TUN device ───────────

    /// <summary>
    /// Sends a real request out through tun0 → tun2socks → SOCKS → phone.
    /// Traffic FROM the tun IP is steered via a dedicated routing table, so this
    /// test never touches (and can never break) the host's normal routing.
    /// </summary>
    private async Task<string?> TestThroughTunAsync(TimeSpan timeout)
    {
        try
        {
            // Steer only traffic sourced from the tun IP into the tunnel, using a
            // private table — the main table (the user's real internet) stays untouched.
            await RunSudoAsync("ip", new[] { "rule", "add", "pref", "100", "from", TunIp, "lookup", "100" });
            await RunSudoAsync("ip", new[] { "route", "add", "default", "dev", DeviceName, "table", "100" });

            try
            {
                var ipv4 = (await Dns.GetHostAddressesAsync(TestHost)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 == null) return null;

                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.Bind(new IPEndPoint(IPAddress.Parse(TunIp), 0));
                using var cts = new CancellationTokenSource(timeout);
                await sock.ConnectAsync(ipv4, 80, cts.Token);
                string http = $"GET /json/ HTTP/1.1\r\nHost: {TestHost}\r\nUser-Agent: tunnel\r\nConnection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(http);
                await sock.SendAsync(hb, SocketFlags.None, cts.Token);
                var buf = new byte[8192];
                int n = await sock.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                string text = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n));
                int q = text.IndexOf("\"query\":\"", StringComparison.Ordinal);
                if (q < 0) return null;
                int start = q + "\"query\":\"".Length;
                int end = text.IndexOf('"', start);
                return end > start ? text[start..end] : null;
            }
            finally
            {
                await RunSudoAsync("ip", new[] { "rule", "del", "pref", "100", "from", TunIp, "lookup", "100" });
                await RunSudoAsync("ip", new[] { "route", "del", "default", "dev", DeviceName, "table", "100" });
            }
        }
        catch (Exception ex)
        {
            AddLog($"TUN egress test error: {ex.Message}");
            return null;
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        int consecutiveFails = 0;
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested) break;

            if (!IsProcessAlive(_tun2SocksPid))
            {
                AddLog("WATCHDOG: tun2socks died — restoring host internet now.");
                await RollbackAsync();
                return;
            }

            tick++;
            // Probe the tunnel every ~6s normally; immediately when a failure is suspected.
            if (consecutiveFails == 0 && tick % 3 != 0) continue;

            string? egress = await TestThroughTunAsync(TimeSpan.FromSeconds(8));
            if (egress == null)
            {
                consecutiveFails++;
                AddLog($"WATCHDOG: tunnel probe failed ({consecutiveFails}/2)");
                if (consecutiveFails >= 2)
                {
                    AddLog("WATCHDOG: tunnel stopped relaying — restoring host internet now.");
                    await RollbackAsync();
                    return;
                }
            }
            else
            {
                consecutiveFails = 0;
            }
        }
    }

    // ── Control-path bypass (cloudflared / Cloudflare) ────────────────────
    // The phone reaches this PC through cloudflared, which keeps an outbound
    // connection to Cloudflare's edge. If Full-Capture tunnels that connection,
    // the relay loops (the tunnel needs the phone, the phone needs the tunnel)
    // and the phone drops instantly. So we pin Cloudflare's ranges to the real
    // gateway: only the phone↔PC control session stays on real internet; all
    // other traffic still goes through tun0 as intended.

    /// <summary>Cloudflare's published IPv4 ranges (https://www.cloudflare.com/ips-v4).</summary>
    private static readonly string[] CloudflareRanges =
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22"
    };

    private async Task InstallBypassRoutesAsync()
    {
        // Detect the real gateway/interface (the Wi-Fi/LAN route that exists
        // BEFORE we install the tun0 default).
        var (gw, dev) = await GetRealGatewayAsync();
        if (gw == null) { AddLog("Bypass: could not detect real gateway — control path may loop."); return; }

        int added = 0;
        foreach (var range in CloudflareRanges)
        {
            try
            {
                await RunSudoAsync("ip", new[] { "route", "add", range, "via", gw, "dev", dev! });
                _bypassRoutes.Add(range);
                added++;
            }
            catch { /* route may already exist */ }
        }
        if (added > 0)
            AddLog($"Bypass: pinned {added} Cloudflare range(s) to {dev} ({gw}) so the phone's control session is never tunneled.");
    }

    private async Task RemoveBypassRoutesAsync()
    {
        foreach (var range in _bypassRoutes)
        {
            try { await RunSudoAsync("ip", new[] { "route", "del", range }); } catch { }
        }
        _bypassRoutes.Clear();
    }

    private static async Task<(string? Gateway, string? Device)> GetRealGatewayAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("ip")
            {
                ArgumentList = { "route", "show", "default" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (null, null);
            string outp = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            foreach (var line in outp.Split('\n'))
            {
                // "default via 192.168.100.1 dev wlan0 proto dhcp src ..."
                int via = line.IndexOf("via ", StringComparison.Ordinal);
                int devIdx = line.IndexOf("dev ", StringComparison.Ordinal);
                if (via < 0 || devIdx < 0) continue;
                string gw = line.Substring(via + 4, line.IndexOf(' ', via + 4) - (via + 4));
                string dev = line.Substring(devIdx + 4, line.IndexOf(' ', devIdx + 4) - (devIdx + 4));
                return (gw, dev);
            }
        }
        catch { }
        return (null, null);
    }

    // ── DNS handling ───────────────────────────────────────────────────────
    private string? SwapDnsForTunnel()
    {
        try
        {
            File.Copy("/etc/resolv.conf", DnsBackupFile, overwrite: true);
            var psi = new ProcessStartInfo("sudo")
            {
                ArgumentList = { "-n", "sh", "-c", "printf 'nameserver 8.8.8.8\\nnameserver 1.1.1.1\\n' > /etc/resolv.conf" },
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void RestoreDns()
    {
        try
        {
            if (!File.Exists(DnsBackupFile)) return;
            var psi = new ProcessStartInfo("sudo")
            {
                ArgumentList = { "-n", "sh", "-c", $"cp {DnsBackupFile} /etc/resolv.conf && rm -f {DnsBackupFile}" },
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }

    // ── Startup sweep (crash recovery) ─────────────────────────────────────

    public async Task SweepStartupAsync()
    {
        try
        {
            if (File.Exists(MarkerFile))
            {
                AddLog("Startup sweep: previous TUN session left active — cleaning up.");
                _active = true;
                _tun2SocksPid = int.TryParse(File.ReadAllText(MarkerFile).Trim(), out int pid) ? pid : 0;
                await RollbackAsync();
            }
            else
            {
                // Belt and braces: kill stray tun2socks / remove orphaned tun0 route.
                await RunSudoAsync("pkill", new[] { "-f", "tun2socks" });
                await RunSudoAsync("ip", new[] { "route", "del", "default", "dev", DeviceName, "metric", RouteMetric.ToString() });
                await RemoveBypassRoutesAsync();
                RestoreDns();
            }
        }
        catch { }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task RunSudoAsync(string program, string[] args)
    {
        var psi = new ProcessStartInfo("sudo")
        {
            ArgumentList = { "-n" },
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add(program);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException($"sudo {program} failed to start");
        string err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
        {
            // Some "del" commands legitimately fail when the route is absent.
            if (!err.Contains("Cannot find device", StringComparison.OrdinalIgnoreCase) &&
                !err.Contains("RTNETLINK", StringComparison.OrdinalIgnoreCase))
                { }
        }
    }
}