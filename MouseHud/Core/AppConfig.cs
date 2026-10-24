using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Flat JSON key/value store at %APPDATA%\MouseHud\config.json.
/// Deliberately global-namespace and API-compatible with G-Helper's AppConfig
/// so the vendored AsusMouse.cs needs no edits.
/// </summary>
public static class AppConfig
{
    private static readonly string ConfigFile = Path.Combine(Logger.AppPath, "config.json");
    private static readonly object _lock = new();
    private static JsonObject _root = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(ConfigFile))
                _root = JsonNode.Parse(File.ReadAllText(ConfigFile))?.AsObject() ?? new JsonObject();
        }
        catch (Exception e)
        {
            Logger.WriteLine("Config load failed, starting fresh: " + e.Message);
            _root = new JsonObject();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Logger.AppPath);
            File.WriteAllText(ConfigFile,
                _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Logger.WriteLine("Config save failed: " + e.Message); }
    }

    public static int Get(string name, int empty = -1)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_root[name] is JsonValue v && v.TryGetValue(out int i)) return i;
            return empty;
        }
    }

    public static string? GetString(string name, string? empty = null)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_root[name] is JsonValue v && v.TryGetValue(out string? s)) return s;
            return empty;
        }
    }

    public static bool GetBool(string name, bool empty = false) => Get(name, empty ? 1 : 0) == 1;

    public static void Set(string name, int value)    { lock (_lock) { EnsureLoaded(); _root[name] = value; Save(); } }
    public static void Set(string name, string value) { lock (_lock) { EnsureLoaded(); _root[name] = value; Save(); } }
    public static void Set(string name, bool value)   => Set(name, value ? 1 : 0);

    public static void Remove(string name) { lock (_lock) { EnsureLoaded(); _root.Remove(name); Save(); } }
}
