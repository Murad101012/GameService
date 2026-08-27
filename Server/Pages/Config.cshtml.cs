using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using PhonesBridge.Services;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Pages;

public class ConfigModel : PageModel
{
    private readonly IHubContext<CommunicateSignalR> _hubContext;
    private readonly ConnectionManager _connectionManager;
    private readonly DeviceConfigManager _deviceConfigManager;
    private static readonly string ConfigPath = Path.Combine("Services", "DataContainers", "config.json");

    public ConfigModel(IHubContext<CommunicateSignalR> hubContext, ConnectionManager connectionManager, DeviceConfigManager deviceConfigManager)
    {
        _hubContext = hubContext;
        _connectionManager = connectionManager;
        _deviceConfigManager = deviceConfigManager;
    }

    [BindProperty]
    public Configuration Config { get; set; } = new();

    public List<string> ConnectedDevices { get; private set; } = new();

    public string? SuccessMessage { get; private set; }

    public void OnGet()
    {
        ViewData["Title"] = "Configuration";
        ViewData["Page"]  = "Config";

        ConnectedDevices = _connectionManager.GetAllDeviceNames();

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

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Configuration";
        ViewData["Page"]  = "Config";

        ConnectedDevices = _connectionManager.GetAllDeviceNames();

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

        SuccessMessage = "Configuration saved and pushed to all connected phones!";
        return Page();
    }
}
