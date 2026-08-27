using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PhonesBridge.Services;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Pages;

public class ActionsModel : PageModel
{
    private readonly ActionManager _actions;
    private readonly ConnectionManager _connectionManager;

    public ActionsModel(ActionManager actions, ConnectionManager connectionManager)
    {
        _actions = actions;
        _connectionManager = connectionManager;
    }

    public List<ActionEntry> Actions { get; private set; } = new();
    public List<DeviceIdentity> Devices { get; private set; } = new();

    public void OnGet()
    {
        ViewData["Title"] = "Actions";
        ViewData["Page"]  = "Actions";
        Actions = _actions.GetAll();
        Devices = _connectionManager.GetAllDevices();
    }
}
