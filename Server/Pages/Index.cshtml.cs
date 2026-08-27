using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PhonesBridge.Services;

namespace PhonesBridge.Pages;

public class IndexModel : PageModel
{
    private readonly ConnectionManager _connectionManager;

    public IndexModel(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Device info rendered server-side into the device sidebar — name plus the
    /// current Domain/P2P link availability. The live refresh (refreshDeviceList)
    /// replaces this markup, but rendering it here avoids a flash of a stale tag.
    /// </summary>
    public List<DeviceLinkInfo> Devices { get; private set; } = new();

    public void OnGet()
    {
        ViewData["Title"] = "File Browser";
        ViewData["Page"]  = "Index";
        Devices = _connectionManager.GetAllDevices()
            .Select(d =>
            {
                var link = _connectionManager.GetLinkStatus(d.DeviceName);
                return new DeviceLinkInfo(d.DeviceName, d.Platform, link.DomainAvailable, link.P2PAvailable);
            })
            .ToList();
    }
}

public record DeviceLinkInfo(string Name, string Platform, bool DomainAvailable, bool P2PAvailable);
