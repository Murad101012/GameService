using Microsoft.AspNetCore.Mvc.RazorPages;
using PhonesBridge.Services;

namespace PhonesBridge.Pages;

public class OfflineModel : PageModel
{
    private readonly CacheManager _cacheManager;
    private readonly ConnectionManager _connectionManager;
    private readonly SettingsManager _settings;

    public OfflineModel(CacheManager cacheManager, ConnectionManager connectionManager, SettingsManager settings)
    {
        _cacheManager = cacheManager;
        _connectionManager = connectionManager;
        _settings = settings;
    }

    /// <summary>
    /// List of cached devices with their info — rendered server-side into device sidebar.
    /// </summary>
    public List<CachedDeviceViewModel> CachedDevices { get; private set; } = new();

    /// <summary>Which media-type counts the offline folder hover popup should show.</summary>
    public bool ShowFileCountImage => _settings.Current.ShowFileCountImage;
    public bool ShowFileCountVideo => _settings.Current.ShowFileCountVideo;
    public bool ShowFileCountAudio => _settings.Current.ShowFileCountAudio;
    public bool ShowFileCountOther => _settings.Current.ShowFileCountOther;

    public void OnGet()
    {
        ViewData["Title"] = "Offline Browser";
        ViewData["Page"]  = "Offline";

        var cachedDevices = _cacheManager.GetAllCachedDevices(_connectionManager);
        CachedDevices = cachedDevices.Select(d => new CachedDeviceViewModel
        {
            DeviceId = d.DeviceId,
            DeviceName = d.DeviceName,
            TotalFileCount = d.TotalFileCount,
            TotalSizeBytes = d.TotalSizeBytes
        }).ToList();
    }
}

public class CachedDeviceViewModel
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int TotalFileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}