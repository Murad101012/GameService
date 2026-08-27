using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using PhonesBridge.Services;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Pages;

/// <summary>
/// Device Manager — one tab that hosts the per-device features (Update, Config)
/// in the same select-a-device → pick-a-feature style as the file browser.
/// </summary>
public class DeviceManagerModel : PageModel
{
    private readonly IHubContext<CommunicateSignalR> _hubContext;
    private readonly ConnectionManager _connectionManager;
    private readonly DeviceConfigManager _deviceConfigManager;
    private static readonly string ConfigPath = Path.Combine("Services", "DataContainers", "config.json");

    public DeviceManagerModel(IHubContext<CommunicateSignalR> hubContext, ConnectionManager connectionManager, DeviceConfigManager deviceConfigManager)
    {
        _hubContext = hubContext;
        _connectionManager = connectionManager;
        _deviceConfigManager = deviceConfigManager;
    }

    [BindProperty]
    public Configuration Config { get; set; } = new();

    /// <summary>Fully connected devices (Bridge/phone registered in the Device List).</summary>
    public List<DeviceIdentity> ConnectedDevices { get; private set; } = new();

    /// <summary>PCs whose standalone Updater is connected even though their device entry is offline.</summary>
    public List<string> UpdaterDevices { get; private set; } = new();

    public string? SuccessMessage { get; private set; }

    public void OnGet()
    {
        ViewData["Title"] = "Device Manager";
        ViewData["Page"]  = "DeviceManager";

        LoadDevices();
        LoadConfig();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Device Manager";
        ViewData["Page"]  = "DeviceManager";

        LoadDevices();

        if (!ModelState.IsValid)
            return Page();

        Config.VideoFrameNormalized = Math.Clamp(Config.VideoFrameNormalized, 0f, 1f);
        Config.ThumbnailQuality = Math.Clamp(Config.ThumbnailQuality, 0f, 1f);
        Config.VideoQuality = Math.Clamp(Config.VideoQuality, 0f, 1f);

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(Config, options);
        await System.IO.File.WriteAllTextAsync(ConfigPath, json);

        // Save per-device config for ALL connected devices
        foreach (var device in _connectionManager.GetAllDevices())
        {
            _deviceConfigManager.SaveDeviceConfig(device.DeviceId, Config);
        }

        await _hubContext.Clients.All.SendAsync("RefreshConfiguration", json);

        SuccessMessage = "Configuration saved and pushed to all connected devices!";
        return Page();
    }

    private void LoadDevices()
    {
        ConnectedDevices = _connectionManager.GetAllDevices();

        // The PC's Update Service carries its OWN connection (never registered as
        // a Device), so it must be an update target even when the Bridge/Device
        // List endpoint for that PC is offline.
        var deviceNames = ConnectedDevices.Select(d => d.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UpdaterDevices = CommunicateSignalR.GetUpdaterDevices()
            .Where(n => !deviceNames.Contains(n!))
            .ToList();
    }

    private void LoadConfig()
    {
        if (System.IO.File.Exists(ConfigPath))
        {
            try
            {
                string json = System.IO.File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<Configuration>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new Configuration();
            }
            catch
            {
                Config = new Configuration();
            }
        }
    }
}
