using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PhonesBridge.Pages;

public class TunnelModel : PageModel
{
    public void OnGet()
    {
        ViewData["Title"] = "VPN / Tunnel";
        ViewData["Page"]  = "Tunnel";
    }
}