using System.Collections.Concurrent;
using System.Text.Json;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

public class WindowsActionManager
{
    private readonly string StorePath = Path.Combine("Services","DataContainers","Actions","windows_actions.json");
    private List<WindowsActionEntry> _actions = new();
    private readonly object _lock = new();

    public WindowsActionManager()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        Load();
        Console.WriteLine($"[WindowsActionManager] loaded {_actions.Count} actions from {StorePath}");
    }

    void Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                _actions = JsonSerializer.Deserialize<List<WindowsActionEntry>>(json) ?? new();
            }
        } catch (Exception ex) { Console.WriteLine($"[WindowsActionManager] load ex {ex.Message}"); }
    }
    void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_actions, new JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(StorePath, json);
            Console.WriteLine($"[WindowsActionManager] saved {_actions.Count} actions");
        } catch (Exception ex) { Console.WriteLine($"[WindowsActionManager] save ex {ex.Message}"); }
    }

    public List<WindowsActionEntry> GetAll() { lock(_lock) return _actions.ToList(); }
    public List<WindowsActionEntry> GetByPc(string pcName) { lock(_lock) return _actions.Where(a=>a.OwnerPc==pcName || string.IsNullOrEmpty(a.OwnerPc)).ToList(); }
    public void Upsert(WindowsActionEntry entry)
    {
        lock(_lock)
        {
            var idx = _actions.FindIndex(a=>a.Id==entry.Id);
            if (idx>=0) _actions[idx]=entry; else _actions.Add(entry);
            Save();
        }
    }
    public bool Delete(string id) { lock(_lock){ var removed=_actions.RemoveAll(a=>a.Id==id)>0; if(removed) Save(); return removed; } }
}
