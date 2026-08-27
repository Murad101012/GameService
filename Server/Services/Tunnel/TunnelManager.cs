using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace PhonesBridge.Services;

/// <summary>
/// A single tunneled TCP stream: the phone opens a REAL socket against its own
/// network (so every destination sees the phone's IP), and the PC pumps bytes
/// through it over the SignalR/WebSocket bridge.
/// </summary>
public sealed class TunnelStream
{
    public int Id { get; }
    public string DeviceName { get; }

    private readonly string _connectionId;
    private readonly TunnelManager _owner;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<(bool Ok, string Message)> _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closeSent;

    internal TunnelStream(int id, string deviceName, string connectionId, TunnelManager owner)
    {
        Id = id;
        DeviceName = deviceName;
        _connectionId = connectionId;
        _owner = owner;
    }

    /// <summary>Completes when the phone reports the socket opened (or failed).</summary>
    public Task<(bool Ok, string Message)> OpenTask => _opened.Task;

    internal void ConfirmOpen(bool ok, string message) => _opened.TrySetResult((ok, message));

    internal void PushFromPhone(byte[] data)
    {
        if (data.Length > 0) _inbound.Writer.TryWrite(data);
    }

    internal void MarkClosedByPhone()
    {
        if (_closeSent == 0) _inbound.Writer.TryComplete();
    }

    internal Task SendOpenAsync(string host, int port)
        => _owner.SendToPhoneAsync(_connectionId, "OpenTunnel", Id, host, port);

    internal Task WriteToPhoneAsync(byte[] data)
        => _owner.SendToPhoneAsync(_connectionId, "TunnelData", Id, data);

    /// <summary>Reads the next chunk of data received from the phone (null = EOF).</summary>
    public async ValueTask<byte[]?> ReadChunkAsync(CancellationToken ct)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeSent, 1) != 0)
        {
            _inbound.Writer.TryComplete();
            return;
        }
        try { await _owner.SendToPhoneAsync(_connectionId, "TunnelClose", Id); }
        catch { }
        _inbound.Writer.TryComplete();
        _owner.RemoveTcp(this);
    }
}

/// <summary>
/// A tunneled UDP relay: the phone owns a DatagramSocket on its own network; the PC
/// forwards (target, payload) items over the bridge so DNS and other UDP traffic
/// egress from the phone's IP.
/// </summary>
public sealed class TunnelUdpStream
{
    public int Id { get; }
    public string DeviceName { get; }

    private readonly string _connectionId;
    private readonly TunnelManager _owner;
    private readonly Channel<(string Host, int Port, byte[] Data)> _inbound = Channel.CreateUnbounded<(string, int, byte[])>();
    private int _closeSent;

    internal TunnelUdpStream(int id, string deviceName, string connectionId, TunnelManager owner)
    {
        Id = id;
        DeviceName = deviceName;
        _connectionId = connectionId;
        _owner = owner;
    }

    internal Task SendOpenAsync() => _owner.SendToPhoneAsync(_connectionId, "OpenUdp", Id);

    internal Task SendToPhoneAsync(string host, int port, byte[] data)
        => _owner.SendToPhoneAsync(_connectionId, "TunnelUdpData", Id, host, port, data);

    internal void PushFromPhone(string host, int port, byte[] data)
    {
        if (data.Length > 0) _inbound.Writer.TryWrite((host, port, data));
    }

    internal void MarkClosedByPhone() => _inbound.Writer.TryComplete();

    /// <summary>Reads the next UDP datagram echoed back from the phone (null = closed).</summary>
    public async ValueTask<(string Host, int Port, byte[] Data)?> ReadChunkAsync(CancellationToken ct)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeSent, 1) != 0)
        {
            _inbound.Writer.TryComplete();
            return;
        }
        try { await _owner.SendToPhoneAsync(_connectionId, "TunnelUdpClose", Id); }
        catch { }
        _inbound.Writer.TryComplete();
        _owner.RemoveUdp(this);
    }
}

/// <summary>
/// Manages tunneled TCP/UDP streams between the PC and a connected phone over the
/// existing SignalR bridge. The phone opens the real sockets, so any traffic the
/// PC sends through a stream is observed by the destination as coming from the
/// phone's IP.
/// </summary>
public sealed class TunnelManager
{
    private readonly ConnectionManager _cm;
    private readonly IHubContext<CommunicateSignalR> _hub;
    private readonly ConcurrentDictionary<int, TunnelStream> _tcp = new();
    private readonly ConcurrentDictionary<int, TunnelUdpStream> _udp = new();
    private int _nextStreamId;

    public TunnelManager(ConnectionManager cm, IHubContext<CommunicateSignalR> hub)
    {
        _cm = cm;
        _hub = hub;
        _cm.DevicesChanged += CloseDeviceStreams;
    }

    public List<string> GetConnectedDevices() => _cm.GetAllDeviceNames();

    public async Task<TunnelStream?> OpenTcpAsync(string deviceName, string host, int port)
    {
        if (!_cm.TryGetConnectionId(deviceName, out var connId)) return null;
        var stream = new TunnelStream(Interlocked.Increment(ref _nextStreamId), deviceName, connId, this);
        _tcp[stream.Id] = stream;
        try
        {
            await _hub.Clients.Client(connId).SendAsync("OpenTunnel", stream.Id, host, port);
        }
        catch
        {
            _tcp.TryRemove(stream.Id, out _);
            await stream.CloseAsync();
            throw;
        }
        return stream;
    }

    public async Task<TunnelUdpStream?> OpenUdpAsync(string deviceName)
    {
        if (!_cm.TryGetConnectionId(deviceName, out var connId)) return null;
        var stream = new TunnelUdpStream(Interlocked.Increment(ref _nextStreamId), deviceName, connId, this);
        _udp[stream.Id] = stream;
        try
        {
            await _hub.Clients.Client(connId).SendAsync("OpenUdp", stream.Id);
        }
        catch
        {
            _udp.TryRemove(stream.Id, out _);
            throw;
        }
        return stream;
    }

    // ── Inbound from the phone (invoked by the hub) ──────────────────────────

    public void OnTunnelData(int streamId, byte[] data)
    {
        if (_tcp.TryGetValue(streamId, out var s)) s.PushFromPhone(data);
    }

    public void OnTunnelStatus(int streamId, bool ok, string message)
    {
        if (_tcp.TryGetValue(streamId, out var s)) s.ConfirmOpen(ok, message);
    }

    public void OnTunnelClosed(int streamId)
    {
        if (_tcp.TryGetValue(streamId, out var s))
        {
            s.MarkClosedByPhone();
            _tcp.TryRemove(streamId, out _);
        }
    }

    public void OnTunnelUdpData(int streamId, string host, int port, byte[] data)
    {
        if (_udp.TryGetValue(streamId, out var s)) s.PushFromPhone(host, port, data);
    }

    public void OnTunnelUdpClosed(int streamId)
    {
        if (_udp.TryGetValue(streamId, out var s))
        {
            s.MarkClosedByPhone();
            _udp.TryRemove(streamId, out _);
        }
    }

    internal Task SendToPhoneAsync(string connectionId, string method, params object?[] args)
        => _hub.Clients.Client(connectionId).SendCoreAsync(method, args, CancellationToken.None);

    internal bool RemoveTcp(TunnelStream s) => _tcp.TryRemove(s.Id, out _);
    internal bool RemoveUdp(TunnelUdpStream s) => _udp.TryRemove(s.Id, out _);

    private void CloseDeviceStreams(string deviceName)
    {
        if (_cm.TryGetConnectionId(deviceName, out _)) return;

        foreach (var s in _tcp.Values)
        {
            if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                s.MarkClosedByPhone();
                _tcp.TryRemove(s.Id, out _);
            }
        }
        foreach (var s in _udp.Values)
        {
            if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                s.MarkClosedByPhone();
                _udp.TryRemove(s.Id, out _);
            }
        }
    }
}