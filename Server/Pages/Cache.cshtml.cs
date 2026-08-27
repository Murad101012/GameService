using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PhonesBridge.Services;

namespace PhonesBridge.Pages;

public class CachePageModel : PageModel
{
    private readonly CacheManager _cacheManager;
    private readonly ConnectionManager _connectionManager;

    public CachePageModel(CacheManager cacheManager, ConnectionManager connectionManager)
    {
        _cacheManager = cacheManager;
        _connectionManager = connectionManager;
    }

    public List<CacheDeviceInfo> CachedDevices { get; private set; } = new();

    public void OnGet()
    {
        ViewData["Title"] = "Cache Management";
        ViewData["Page"]  = "Cache";
        CachedDevices = _cacheManager.GetAllCachedDevices(_connectionManager);
    }
}
