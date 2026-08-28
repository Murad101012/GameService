using System.Collections.Concurrent;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

public class ConnectionManager
{
    /// <summary>Raised when the connected device set changes (connect or disconnect).</summary>
    public event Action<string>? DevicesChanged;

    /// <summary>Raised when a device's Domain/P2P link availability changes (upgrade or drop).</summary>
    public event Action<string>? LinkStatusChanged;

    private readonly ConcurrentDictionary<string, DeviceIdentity> _deviceToIdentityMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToDeviceMap = new();
    private readonly ConcurrentDictionary<string, DeviceLinkStatus> _linkStatusMap = new(StringComparer.OrdinalIgnoreCase);

    public void AddNewConnectedDevice(DeviceIdentity device)
    {
        // Remove old connection mapping if device reconnected with a new ConnectionId
        if (_deviceToIdentityMap.TryGetValue(device.DeviceName, out var oldIdentity) && oldIdentity.ConnectionId != device.ConnectionId)
        {
            _connectionToDeviceMap.TryRemove(oldIdentity.ConnectionId, out _);
        }

        _deviceToIdentityMap[device.DeviceName] = device;
        _connectionToDeviceMap[device.ConnectionId] = device.DeviceName;
        _linkStatusMap[device.DeviceName] = new DeviceLinkStatus { DomainAvailable = true, P2PAvailable = false };
        /* Console.WriteLine($"[ConnectionManager] Device mapped: {device.DeviceName} (ID: {device.DeviceId}) -> Connection {device.ConnectionId}"); */
        DevicesChanged?.Invoke(device.DeviceName);
        LinkStatusChanged?.Invoke(device.DeviceName);
    }

    public void RemoveConnectedDeviceByConnectionId(string connectionId)
    {
        if (_connectionToDeviceMap.TryRemove(connectionId, out var deviceName))
        {
            _deviceToIdentityMap.TryRemove(deviceName, out _);
            _linkStatusMap.TryRemove(deviceName, out _);
            /* Console.WriteLine($"[ConnectionManager] Removed device endpoint: {deviceName}"); */
            DevicesChanged?.Invoke(deviceName);
            LinkStatusChanged?.Invoke(deviceName);
        }
    }

    public bool TryGetConnectionId(string deviceName, out string connectionId)
    {
        if (_deviceToIdentityMap.TryGetValue(deviceName, out var identity))
        {
            connectionId = identity.ConnectionId;
            return true;
        }
        connectionId = string.Empty;
        return false;
    }

    public bool TryGetDeviceIdentity(string deviceName, out DeviceIdentity? identity)
    {
        return _deviceToIdentityMap.TryGetValue(deviceName, out identity);
    }

    public List<string> GetAllDeviceNames()
    {
        return _deviceToIdentityMap.Keys.ToList();
    }

    public List<DeviceIdentity> GetAllDevices()
    {
        return _deviceToIdentityMap.Values.ToList();
    }

    /// <summary>Set whether the given device currently has a verified P2P datapath. Raises LinkStatusChanged on change.</summary>
    public void SetP2PAvailable(string deviceName, bool available)
    {
        if (_linkStatusMap.TryGetValue(deviceName, out var status) && status.P2PAvailable != available)
        {
            status.P2PAvailable = available;
            /* Console.WriteLine($"[ConnectionManager] P2P link for {deviceName}: {(available ? "available" : "dropped")}"); */
            LinkStatusChanged?.Invoke(deviceName);
        }
    }

    public DeviceLinkStatus GetLinkStatus(string deviceName)
    {
        return _linkStatusMap.TryGetValue(deviceName, out var status) ? status : new DeviceLinkStatus { DomainAvailable = false, P2PAvailable = false };
    }
}

/// <summary>Availability of the two connection paths for a device: the Cloudflare Domain relay and a direct P2P link.</summary>
public class DeviceLinkStatus
{
    public bool DomainAvailable { get; set; }
    public bool P2PAvailable { get; set; }
}