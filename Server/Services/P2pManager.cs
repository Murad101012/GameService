using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PhonesBridge.Services;

/// <summary>
/// Direct (P2P) datapath. The phone dials the PC's LAN candidate addresses over
/// TCP; the PC verifies the link with a bidirectional echo before ever declaring
/// it "available", then serves file chunks over that socket instead of the
/// Cloudflare Domain relay. A heartbeat watchdog silently drops a stale link and
/// the UI falls back to Domain — P2P can never block the Domain path.
///
/// Wire protocol (newline text + raw binary payloads, one long-lived socket):
///   phone→PC   HELLO <deviceId>
///   PC→phone   OK <deviceName> | ERR <reason>
///   PC→phone   ECHO <nonce>      phone→PC   ECHO <nonce>   (bidirectional verify)
///   PC→phone   PING <nonce>      phone→PC   PONG <nonce>   (heartbeat, both ways)
///   PC→phone   FILE <reqId> <path64> <offset> <count> <lowRes> <frame> <quality>
///   phone→PC   DATA <reqId> <length> + raw bytes | ERR <reqId> <reason>
/// </summary>
public class P2pManager
{
    public const int DefaultPort = 5201;

    private readonly ConnectionManager _connectionManager;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, P2pSession> _sessionsByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> _pendingRequests = new();
    private int _nextRequestId;

    public P2pManager(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        _listener = new TcpListener(IPAddress.Any, DefaultPort);
    }

    public int Port => DefaultPort;

    public void Start()
    {
        try
        {
            _listener.Start();
            /* Console.WriteLine($"[P2P] Listening for direct phone connections on 0.0.0.0:{DefaultPort}"); */
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[P2P] Failed to bind listener on port {DefaultPort}: {ex.Message}"); */
            return;
        }

        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(HeartbeatWatchdogAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var session in _sessionsByDevice.Values) session.Dispose();
        _sessionsByDevice.Clear();
    }

    // ── Candidate discovery ───────────────────────────────────────────────────
    // LAN IPv4 addresses on interfaces with a default gateway; loopback/virtual
    // adapters are skipped. Public (STUN) candidates slot in here later.

    public List<string> GetCandidateEndpoints()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip)) continue;
                    if (ip.GetAddressBytes()[0] == 169) continue; // link-local
                    ips.Add(ip.ToString());
                }
            }
        }
        catch { }

        if (ips.Count == 0) ips.Add("127.0.0.1");
        return ips.Distinct().Select(ip => $"{ip}:{DefaultPort}").ToList();
    }

    // ── Accept / handshake / echo verification ────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { continue; }

            _ = Task.Run(() => HandleIncomingAsync(client));
        }
    }

    private async Task HandleIncomingAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            // ── Handshake: phone announces its DeviceId ──
            string? hello = await ReadLineAsync(stream, TimeSpan.FromSeconds(10));
            if (hello == null || !hello.StartsWith("HELLO ", StringComparison.Ordinal))
            {
                await TryWriteLineAsync(stream, "ERR bad-handshake");
                return;
            }

            string deviceId = hello["HELLO ".Length..].Trim();
            var identity = _connectionManager.GetAllDevices().FirstOrDefault(d =>
                d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));

            if (identity == null)
            {
                await TryWriteLineAsync(stream, "ERR unknown-device");
                return;
            }

            await TryWriteLineAsync(stream, $"OK {identity.DeviceName}");

            // ── Bidirectional echo verification: only a real, working round-trip
            //    ever makes the link "available". Never an optimistic "connecting". ──
            string nonce = Guid.NewGuid().ToString("N");
            await TryWriteLineAsync(stream, $"ECHO {nonce}");
            string? echo = await ReadLineAsync(stream, TimeSpan.FromSeconds(10));
            if (echo == null || !echo.Equals($"ECHO {nonce}", StringComparison.Ordinal))
            {
                await TryWriteLineAsync(stream, "ERR echo-failed");
                return;
            }

            var session = new P2pSession(identity.DeviceName, client, stream);
            _sessionsByDevice[identity.DeviceName] = session;
            _connectionManager.SetP2PAvailable(identity.DeviceName, true);
            /* Console.WriteLine($"[P2P] Verified direct link with {identity.DeviceName} on {client.Client.RemoteEndPoint}"); */

            // ── Single reader: dispatches heartbeats + chunk responses ──
            await ReadDispatchLoopAsync(session);
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[P2P] Session error: {ex.Message}"); */
        }
        finally
        {
            var doomed = _sessionsByDevice.FirstOrDefault(kv => kv.Value.Client == client);
            if (doomed.Key != null)
            {
                _sessionsByDevice.TryRemove(doomed.Key, out _);
                _connectionManager.SetP2PAvailable(doomed.Key, false);
                /* Console.WriteLine($"[P2P] Direct link with {doomed.Key} closed."); */
            }
            foreach (var tcs in _pendingRequests.Values) tcs.TrySetResult(null);
            _pendingRequests.Clear();
            client.Dispose();
        }
    }

    private async Task ReadDispatchLoopAsync(P2pSession session)
    {
        var stream = session.Stream;
        while (!_cts.IsCancellationRequested)
        {
            string? line = await ReadLineAsync(stream, TimeSpan.FromSeconds(60));
            if (line == null) break;

            session.Touch();

            if (line.StartsWith("PING ", StringComparison.Ordinal))
            {
                await session.WriteLineAsync($"PONG {line["PING ".Length..]}");
                continue;
            }
            if (line.StartsWith("ECHO ", StringComparison.Ordinal))
            {
                await session.WriteLineAsync(line); // echo straight back
                continue;
            }
            if (line.StartsWith("PONG ", StringComparison.Ordinal)) continue; // our ping answered
            if (line.StartsWith("DATA ", StringComparison.Ordinal))
            {
                // DATA <reqId> <length>
                var parts = line.Split(' ', 3);
                if (parts.Length == 3 && int.TryParse(parts[1], out int reqId) && int.TryParse(parts[2], out int len))
                {
                    byte[] payload = new byte[len];
                    int read = 0;
                    while (read < len)
                    {
                        int n = await stream.ReadAsync(payload.AsMemory(read, len - read), _cts.Token);
                        if (n == 0) throw new IOException("P2P stream closed mid-payload");
                        read += n;
                    }
                    /* Console.WriteLine($"[P2P] Chunk served over direct link ({len} bytes, req {reqId})"); */
                    CompleteRequest(reqId, payload);
                }
                continue;
            }
            if (line.StartsWith("ERR ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int reqId)) CompleteRequest(reqId, null);
                continue;
            }
        }
    }

    private void CompleteRequest(int reqId, byte[]? data)
    {
        if (_pendingRequests.TryRemove(reqId, out var tcs)) tcs.TrySetResult(data);
    }

    // ── Chunk request (called by web endpoints when FileRoute == P2P) ─────────

    /// <summary>
    /// Request a file chunk over the P2P link. Returns the bytes, or null when the
    /// link is unavailable or the phone replied with an error — callers must fall
    /// back to the Domain/SignalR path.
    /// </summary>
    public async Task<byte[]?> RequestChunkAsync(
        string deviceName, string path, long offset, int count,
        bool lowRes, float frame, float quality, CancellationToken ct)
    {
        if (!_sessionsByDevice.TryGetValue(deviceName, out var session)) return null;
        if (session.IsStale) return null;

        int reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[reqId] = tcs;

        string path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        string frameS = frame.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string qualityS = quality.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string cmd = $"FILE {reqId} {path64} {offset} {count} {(lowRes ? 1 : 0)} {frameS} {qualityS}";

        try
        {
            await session.WriteLineAsync(cmd);
            using var reg = ct.Register(() => tcs.TrySetResult(null));
            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            /* Console.WriteLine($"[P2P] Chunk request {reqId} for {path} @{offset}: {(result == null ? "FAILED" : $"{result.Length}b over direct link")}"); */
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(reqId, out _);
        }
    }

    // ── Heartbeat watchdog ────────────────────────────────────────────────────
    // Drops links that stopped responding so the UI's tag goes grey and traffic
    // falls back to the Domain relay without any user intervention.

    private async Task HeartbeatWatchdogAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); }
            catch (OperationCanceledException) { break; }

            foreach (var kv in _sessionsByDevice)
            {
                if (kv.Value.IsStale)
                {
                    /* Console.WriteLine($"[P2P] Direct link with {kv.Key} is stale — dropping."); */
                    kv.Value.Dispose();
                }
            }
        }
    }

    /// <summary>Dispose sessions for a device that disconnected from the Domain channel.</summary>
    public void DeviceDisconnected(string deviceName)
    {
        if (_sessionsByDevice.TryRemove(deviceName, out var session))
        {
            session.Dispose();
            _connectionManager.SetP2PAvailable(deviceName, false);
        }
    }

    public bool IsAvailable(string deviceName)
    {
        return _sessionsByDevice.ContainsKey(deviceName) && _connectionManager.GetLinkStatus(deviceName).P2PAvailable;
    }

    // ── Line protocol helpers ─────────────────────────────────────────────────

    private static async Task<string?> ReadLineAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        var buffer = new byte[1];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
                if (read == 0) return null;
                if (buffer[0] == '\n') break;
                if (buffer[0] != '\r') sb.Append((char)buffer[0]);
                if (sb.Length > 8192) return null;
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private static async Task TryWriteLineAsync(NetworkStream stream, string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes.AsMemory());
            await stream.FlushAsync();
        }
        catch { }
    }

    private class P2pSession
    {
        public string DeviceName { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private long _lastSeenTicks;

        public P2pSession(string deviceName, TcpClient client, NetworkStream stream)
        {
            DeviceName = deviceName;
            Client = client;
            Stream = stream;
            _lastSeenTicks = Environment.TickCount64;
        }

        public void Touch() => Interlocked.Exchange(ref _lastSeenTicks, Environment.TickCount64);

        public bool IsStale => Environment.TickCount64 - Interlocked.Read(ref _lastSeenTicks) > 25_000;

        public async Task WriteLineAsync(string line)
        {
            await _writeLock.WaitAsync();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await Stream.WriteAsync(bytes.AsMemory());
                await Stream.FlushAsync();
            }
            catch { }
            finally { _writeLock.Release(); }
        }

        public void Dispose()
        {
            try { Client.Dispose(); } catch { }
            _writeLock.Dispose();
        }
    }
}