using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhonesBridge.Services;

/// <summary>
/// A SOCKS5 proxy bound to 127.0.0.1 that relays every connection through a
/// connected phone via <see cref="TunnelManager"/>. The phone opens the real
/// sockets on its own network, so the destination sees the phone's IP, never the
/// PC's. Supports CONNECT and UDP ASSOCIATE (DNS + UDP apps).
/// </summary>
public sealed class Socks5ProxyServer
{
    private const int RelayChunkSize = 16384;
    private readonly TunnelManager _tunnels;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Socks5ProxyServer(TunnelManager tunnels)
    {
        _tunnels = tunnels;
    }

    public int BoundPort { get; private set; }
    public bool IsRunning { get; private set; }
    public long TcpConnections;
    public long UdpAssociations;

    public void Start(int port = 5190, bool retryOnBusy = true)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();

        const int maxTries = 10;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _listener = listener;
                break;
            }
            catch (SocketException ex) when (retryOnBusy && attempt < maxTries &&
                                             (ex.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                                              ex.SocketErrorCode == SocketError.AccessDenied))
            {
                Thread.Sleep(500);
            }
        }

        BoundPort = port;
        IsRunning = true;
        /* Console.WriteLine($"[Tunnel] SOCKS5 proxy listening on 127.0.0.1:{port}"); */
        _loop = Task.Run(() => AcceptLoopAsync(_listener!, _cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        /* Console.WriteLine("[Tunnel] SOCKS5 proxy stopped."); */
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(200, ct); continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using (tcp)
        {
            try
            {
                var net = tcp.GetStream();

                // ── Greeting: VER=5, methods ──
                var hdr = new byte[2];
                if (!await ReadExactAsync(net, hdr, ct) || hdr[0] != 0x05) return;
                var methods = new byte[hdr[1]];
                if (!await ReadExactAsync(net, methods, ct)) return;
                await net.WriteAsync(new byte[] { 0x05, 0x00 }, 0, 2, ct);

                // ── Request ──
                var req = new byte[4];
                if (!await ReadExactAsync(net, req, ct) || req[0] != 0x05) return;
                byte command = req[1];
                byte atyp = req[3];

                string host;
                switch (atyp)
                {
                    case 0x01:
                    {
                        var ip = new byte[4];
                        if (!await ReadExactAsync(net, ip, ct)) return;
                        host = new IPAddress(ip).ToString();
                        break;
                    }
                    case 0x03:
                    {
                        var lenb = new byte[1];
                        if (!await ReadExactAsync(net, lenb, ct)) return;
                        var name = new byte[lenb[0]];
                        if (!await ReadExactAsync(net, name, ct)) return;
                        host = Encoding.UTF8.GetString(name);
                        break;
                    }
                    case 0x04:
                    {
                        var ip = new byte[16];
                        if (!await ReadExactAsync(net, ip, ct)) return;
                        host = new IPAddress(ip).ToString();
                        break;
                    }
                    default:
                        await ReplyAsync(net, 0x08, ct);
                        return;
                }

                var pb = new byte[2];
                if (!await ReadExactAsync(net, pb, ct)) return;
                int port = BinaryPrimitives.ReadUInt16BigEndian(pb);

                if (command == 0x01) await HandleConnectAsync(net, host, port, ct);
                else if (command == 0x03) await HandleUdpAssociateAsync(tcp, net, ct);
                else await ReplyAsync(net, 0x07, ct); // command not supported
            }
            catch (Exception ex) { /*Console.WriteLine($"[Tunnel] socks client error: {ex.Message}"); */ }
        }
    }

    private async Task HandleConnectAsync(NetworkStream net, string host, int port, CancellationToken ct)
    {
        string? device = ResolveDevice();
        if (device == null)
        {
            await ReplyAsync(net, 0x05, ct);
            return;
        }

        TunnelStream? stream;
        try { stream = await _tunnels.OpenTcpAsync(device, host, port); }
        catch { stream = null; }

        if (stream == null)
        {
            await ReplyAsync(net, 0x05, ct);
            return;
        }

        try
        {
            (bool ok, string _) = await stream.OpenTask.WaitAsync(TimeSpan.FromSeconds(30));
            if (!ok)
            {
                await stream.CloseAsync();
                await ReplyAsync(net, 0x05, ct);
                return;
            }
        }
        catch (TimeoutException)
        {
            await stream.CloseAsync();
            await ReplyAsync(net, 0x05, ct);
            return;
        }

        Interlocked.Increment(ref TcpConnections);
        await ReplyAsync(net, 0x00, ct);
        await RelayTcpAsync(net, stream, ct);
    }

    private async Task HandleUdpAssociateAsync(TcpClient tcp, NetworkStream net, CancellationToken ct)
    {
        string? device = ResolveDevice();
        if (device == null)
        {
            await ReplyAsync(net, 0x05, ct);
            return;
        }

        var stream = await _tunnels.OpenUdpAsync(device);
        if (stream == null)
        {
            await ReplyAsync(net, 0x05, ct);
            return;
        }

        using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int relayPort = ((IPEndPoint)relay.Client.LocalEndPoint!).Port;
        var reply = new byte[10] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 };
        reply[8] = (byte)(relayPort >> 8);
        reply[9] = (byte)(relayPort & 0xFF);
        await net.WriteAsync(reply, 0, reply.Length, ct);

        Interlocked.Increment(ref UdpAssociations);
        IPEndPoint? clientEp = null;

        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toPhone = Task.Run(async () =>
        {
            try
            {
                while (!relayCts.IsCancellationRequested)
                {
                    var res = await relay.ReceiveAsync(relayCts.Token);
                    clientEp ??= res.RemoteEndPoint;
                    var parsed = ParseUdpFrame(res.Buffer);
                    if (parsed != null)
                    {
                        await stream.SendToPhoneAsync(parsed.Value.Host, parsed.Value.Port, parsed.Value.Data);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        var toClient = Task.Run(async () =>
        {
            try
            {
                while (!relayCts.IsCancellationRequested)
                {
                    var item = await stream.ReadChunkAsync(relayCts.Token);
                    if (item == null) break;
                    if (clientEp != null)
                    {
                        var frame = BuildUdpReplyFrame(item.Value.Host, item.Value.Port, item.Value.Data);
                        await relay.SendAsync(frame, frame.Length, clientEp);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        // The association lives while the control TCP connection stays open.
        try { await Task.WhenAny(toPhone, toClient, AwaitTcpCloseAsync(net, ct)); }
        catch { }
        relayCts.Cancel();
        await stream.CloseAsync();
    }

    private string? ResolveDevice()
    {
        List<string> all;
        try { all = _tunnels.GetConnectedDevices(); }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Tunnel][socks] ResolveDevice exc: {ex.Message}"); */
            return null;
        }
        if (all.Count == 0) return null;
        // Prefer the explicitly selected phone, else the first connected device.
        return all.FirstOrDefault(d => string.Equals(d, SelectedDevice, StringComparison.OrdinalIgnoreCase)) ?? all[0];
    }

    /// <summary>Connects to ip-api.com through the proxy and reports the egress IP
    /// the destination sees (i.e. the phone's public IP). Verifies the whole TCP
    /// relay path including the tunneled CONNECT.</summary>
    /// <summary>Opens a tunneled connection to ip-api.com through the selected phone
/// and reports the egress IP the destination sees (i.e. the phone's public IP).
/// Uses the same tunnel transport the SOCKS relay uses, without needing the app
/// to dial back into its own listener.</summary>
public async Task<string?> TestEgressAsync(CancellationToken ct = default)
    {
        string? device = ResolveDevice();
        if (device == null) return null;

        TunnelStream? stream;
        try { stream = await _tunnels.OpenTcpAsync(device, "ip-api.com", 80); }
        catch { stream = null; }
        if (stream == null) return null;

        try
        {
            (bool ok, string _) = await stream.OpenTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (!ok) return null;
        }
        catch (TimeoutException) { await stream.CloseAsync(); return null; }

        var http = "GET /json/ HTTP/1.1\r\nHost: ip-api.com\r\nUser-Agent: tunnel\r\nConnection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(http);
        await stream.WriteToPhoneAsync(hb);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var chunk = await stream.ReadChunkAsync(cts.Token);
                if (chunk == null) break;
                ms.Write(chunk, 0, chunk.Length);
                if (ms.Length > 65536) break;
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        await stream.CloseAsync();
        if (ms.Length == 0) return null;

        string text = Encoding.ASCII.GetString(ms.ToArray());
        int q = text.IndexOf("\"query\":\"", StringComparison.Ordinal);
        if (q < 0) return text.Split('\n').FirstOrDefault()?.Trim();
        int start = q + "\"query\":\"".Length;
        int end = text.IndexOf('"', start);
        return end > start ? text[start..end] : null;
    }

    /// <summary>Device the user picked on the Tunnel page (null = use first connected).</summary>
    public string? SelectedDevice { get; set; }

    // ── Relay helpers ─────────────────────────────────────────────────────────

    private async Task RelayTcpAsync(NetworkStream net, TunnelStream stream, CancellationToken ct)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toPhone = PumpAsync(
            () => ReadChunkFromClientAsync(net, relayCts.Token),
            b => stream.WriteToPhoneAsync(b),
            relayCts.Token);
        var toClient = PumpAsync(
            () => stream.ReadChunkAsync(relayCts.Token),
            async b => { await net.WriteAsync(b, 0, b.Length, relayCts.Token); await net.FlushAsync(relayCts.Token); },
            relayCts.Token);
        await Task.WhenAny(toPhone, toClient);
        relayCts.Cancel();
        try { await Task.WhenAll(toPhone, toClient); } catch { }
        await stream.CloseAsync();
    }

    private static async Task PumpAsync(Func<ValueTask<byte[]?>> read, Func<byte[], Task> write, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var chunk = await read();
                if (chunk == null) break;
                await write(chunk);
            }
        }
        catch { }
    }

    private static async ValueTask<byte[]?> ReadChunkFromClientAsync(NetworkStream net, CancellationToken ct)
    {
        var buf = new byte[RelayChunkSize];
        int n;
        try { n = await net.ReadAsync(buf, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        if (n <= 0) return null;
        return n == buf.Length ? buf : buf.AsSpan(0, n).ToArray();
    }

    private static async Task AwaitTcpCloseAsync(NetworkStream net, CancellationToken ct)
    {
        try
        {
            var one = new byte[1];
            while (true)
            {
                if (await net.ReadAsync(one, ct) <= 0) return;
            }
        }
        catch { }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream net, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n;
            try { n = await net.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct); }
            catch { return false; }
            if (n <= 0) return false;
            offset += n;
        }
        return true;
    }

    private static Task ReplyAsync(NetworkStream net, byte status, CancellationToken ct)
    {
        var b = new byte[10] { 0x05, status, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        return net.WriteAsync(b, 0, b.Length, ct);
    }

    // ── SOCKS5 UDP frame helpers ─────────────────────────────────────────────

    private static (string Host, int Port, byte[] Data)? ParseUdpFrame(byte[] frame)
    {
        if (frame.Length < 4) return null;
        // RSV(2) must be 0, FRAG(1) must be 0 (we don't implement fragmentation).
        if (frame[2] != 0x00) return null;
        byte atyp = frame[3];
        int idx = 4;
        string host;
        switch (atyp)
        {
            case 0x01:
                if (frame.Length < 8) return null;
                host = new IPAddress(frame.AsSpan(idx, 4)).ToString();
                idx += 4;
                break;
            case 0x03:
                if (frame.Length < 5) return null;
                int len = frame[idx++];
                if (frame.Length < idx + len + 2) return null;
                host = Encoding.UTF8.GetString(frame, idx, len);
                idx += len;
                break;
            case 0x04:
                if (frame.Length < 20) return null;
                host = new IPAddress(frame.AsSpan(idx, 16)).ToString();
                idx += 16;
                break;
            default:
                return null;
        }
        if (frame.Length < idx + 2) return null;
        int port = frame[idx] << 8 | frame[idx + 1];
        idx += 2;
        return (host, port, frame[idx..]);
    }

    private static byte[] BuildUdpReplyFrame(string host, int port, byte[] data)
    {
        using var ms = new MemoryStream(4 + 3 + 16 + 2 + data.Length);
        ms.WriteByte(0x00); ms.WriteByte(0x00); // RSV
        ms.WriteByte(0x00);                     // FRAG
        if (System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            ms.WriteByte(0x01); ms.Write(ip.GetAddressBytes(), 0, 4);
        }
        else if (System.Net.IPAddress.TryParse(host, out var ip6) && ip6.AddressFamily == AddressFamily.InterNetworkV6)
        {
            ms.WriteByte(0x04); ms.Write(ip6.GetAddressBytes(), 0, 16);
        }
        else
        {
            byte[] name = Encoding.UTF8.GetBytes(host);
            ms.WriteByte(0x03); ms.WriteByte((byte)name.Length); ms.Write(name, 0, name.Length);
        }
        ms.WriteByte((byte)(port >> 8)); ms.WriteByte((byte)(port & 0xFF));
        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}