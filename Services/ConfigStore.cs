using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCodeTelegramBridge.Models;

namespace OpenCodeTelegramBridge.Services;

/// <summary>Loads/saves AppConfig as data/config.json next to the executable.</summary>
public class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DataDir { get; }
    public string ConfigPath { get; }

    private readonly object _lock = new();
    private AppConfig _current;

    public ConfigStore(IWebHostEnvironment env)
    {
        DataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(DataDir);
        ConfigPath = Path.Combine(DataDir, "config.json");
        _current = LoadFromDisk();
    }

    public AppConfig Current
    {
        get { lock (_lock) return _current; }
    }

    private AppConfig LoadFromDisk()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        lock (_lock)
        {
            _current = config;
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
    }
}
