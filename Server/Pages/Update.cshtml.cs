using Microsoft.AspNetCore.Mvc.RazorPages;
using PhonesBridge.Services;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Pages;

public class UpdateModel : PageModel
{
    private readonly ConnectionManager _connectionManager;

    public UpdateModel(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public List<DeviceIdentity> ConnectedDevices { get; private set; } = new();
    public List<string> UpdaterDevices { get; private set; } = new();

    public void OnGet()
    {
        ViewData["Title"] = "Update Service";
        ViewData["Page"]  = "Update";

        ConnectedDevices = _connectionManager.GetAllDevices();

        // The PC's Update Service carries its OWN connection (never registered as
        // a Device), so it must be an update target even when the Bridge/Device
        // List endpoint for that PC is offline.
        var deviceNames = _connectionManager.GetAllDeviceNames();
        UpdaterDevices = CommunicateSignalR.GetUpdaterDevices()
            .Where(n => !deviceNames.Contains(n!))
            .ToList();
    }
}
