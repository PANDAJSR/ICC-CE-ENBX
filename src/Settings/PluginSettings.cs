using System.IO;
using System.Text.Json;

namespace ICC.CE.ENBX;

public sealed class PluginSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool ClickToNextEnabled { get; set; } = true;
    public bool SwipeNavigationEnabled { get; set; } = true;
    public bool CloudLazyLoadingEnabled { get; set; } = true;

    public static PluginSettings Load(string folder)
    {
        try
        {
            var filePath = Path.Combine(folder, "settings.json");
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<PluginSettings>(json, JsonOptions);
                if (settings != null) return settings;
            }
        }
        catch
        {
            // Fallback to default
        }

        return new PluginSettings();
    }

    public void Save(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, "settings.json");
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Silently ignore save errors
        }
    }
}
