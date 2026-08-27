using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PhonesBridge.Services;

namespace PhonesBridge.Pages;

public class SettingsModel : PageModel
{
    private readonly SettingsManager _settings;
    private readonly CacheManager _cacheManager;

    public SettingsModel(SettingsManager settings, CacheManager cacheManager)
    {
        _settings = settings;
        _cacheManager = cacheManager;
    }

    [BindProperty]
    public bool EnableWebDavUrls { get; set; }

    [BindProperty]
    public int Port { get; set; } = 8052;

    [BindProperty]
    public bool AllowLanAccess { get; set; }

    [BindProperty]
    public bool CacheImages { get; set; }

    [BindProperty]
    public bool CacheVideos { get; set; }

    [BindProperty]
    public bool CacheAudio { get; set; }

    [BindProperty]
    public bool CacheFiles { get; set; }

    [BindProperty]
    public string SizeDisplayMode { get; set; } = "both";

    [BindProperty]
    public string SizeDisplayTarget { get; set; } = "both";

    [BindProperty]
    public bool ShowFileCountImage { get; set; }

    [BindProperty]
    public bool ShowFileCountVideo { get; set; }

    [BindProperty]
    public bool ShowFileCountAudio { get; set; }

    [BindProperty]
    public bool ShowFileCountOther { get; set; }

    [BindProperty]
    public string CacheLimitMode { get; set; } = "Unlimited";

    [BindProperty]
    public int CacheLimitMb { get; set; } = 1024;

    public string? SuccessMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>When non-zero, the port changed and the page should redirect here.</summary>
    public int RedirectPort { get; private set; }

    public List<string> LanUrls { get; private set; } = new();

    public void OnGet()
    {
        ViewData["Title"] = "Settings";
        ViewData["Page"]  = "Settings";
        var s = _settings.Current;
        EnableWebDavUrls = s.EnableWebDavUrls;
        Port            = s.Port;
        AllowLanAccess  = s.AllowLanAccess;
        CacheImages     = s.CacheImages;
        CacheVideos     = s.CacheVideos;
        CacheAudio      = s.CacheAudio;
        CacheFiles      = s.CacheFiles;
        SizeDisplayMode = s.SizeDisplayMode;
        SizeDisplayTarget = s.SizeDisplayTarget;
        ShowFileCountImage = s.ShowFileCountImage;
        ShowFileCountVideo = s.ShowFileCountVideo;
        ShowFileCountAudio = s.ShowFileCountAudio;
        ShowFileCountOther  = s.ShowFileCountOther;
        CacheLimitMode = s.CacheLimitMode;
        CacheLimitMb = s.CacheLimitMb;
        LanUrls = SettingsManager.GetLocalLanUrls(s.Port);
    }

    public IActionResult OnPost()
    {
        ViewData["Title"] = "Settings";
        ViewData["Page"]  = "Settings";

        var s = _settings.Current;

        bool portChanged = Port != s.Port;
        bool lanChanged  = AllowLanAccess != s.AllowLanAccess;
        if (Port is < 1 or > 65535)
        {
            ErrorMessage = $"Invalid port '{Port}' — must be between 1 and 65535.";
            Port = s.Port;
            portChanged = false;
        }

        s.EnableWebDavUrls = EnableWebDavUrls;
        s.CacheImages      = CacheImages;
        s.CacheVideos      = CacheVideos;
        s.CacheAudio       = CacheAudio;
        s.CacheFiles       = CacheFiles;
        s.SizeDisplayMode  = string.IsNullOrEmpty(SizeDisplayMode) ? "both" : SizeDisplayMode;
        s.SizeDisplayTarget = string.IsNullOrEmpty(SizeDisplayTarget) ? "both" : SizeDisplayTarget;
        s.ShowFileCountImage = ShowFileCountImage;
        s.ShowFileCountVideo = ShowFileCountVideo;
        s.ShowFileCountAudio = ShowFileCountAudio;
        s.ShowFileCountOther  = ShowFileCountOther;
        s.CacheLimitMode = string.IsNullOrEmpty(CacheLimitMode) ? "Unlimited" : CacheLimitMode;
        s.CacheLimitMb = Math.Clamp(CacheLimitMb, 100, 10000);
        s.AllowLanAccess   = AllowLanAccess;

        // Populate LAN URLs for the view (after potential change)
        LanUrls = SettingsManager.GetLocalLanUrls(Port);
        // Also update bound properties for view
        CacheLimitMode = s.CacheLimitMode;
        CacheLimitMb = s.CacheLimitMb;

        bool needsRestart = portChanged || lanChanged;
        if (needsRestart)
        {
            // Persist + sync Kestrel config + restart
            // Avoid double-save: ScheduleRestart will save again
            _settings.Current = s;
            RedirectPort = Port;
            _settings.ScheduleRestart(Port, AllowLanAccess);
            if (portChanged && lanChanged)
                SuccessMessage = $"Port {Port} and LAN access {(AllowLanAccess ? "enabled" : "disabled")} — server restarting. Reopen at http://{(AllowLanAccess ? "<lan-ip>" : "127.0.0.1")}:{Port}";
            else if (portChanged)
                SuccessMessage = $"Port changed to {Port} — the server is restarting and will redirect you there.";
            else
                SuccessMessage = $"LAN access {(AllowLanAccess ? "enabled" : "disabled")} — server restarting. {(AllowLanAccess ? $"Open from phone: {string.Join(", ", LanUrls.Take(2))}" : "Only this PC can open the WebUI now.")}";
        }
        else
        {
            _settings.Current = s;
            SuccessMessage = "Settings saved.";
        }

        if (s.CacheLimitMode == "Custom")
        {
            try { _cacheManager.EnforceCacheLimit(s.CacheLimitMb); } catch { }
        }

        return Page();
    }
}