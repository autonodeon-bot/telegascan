using System.IO;
using System.Text.Json;

namespace TelegaScan.Services;

public sealed class AppSettingsStore
{
    private static readonly string PathJson = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegaScan",
        "settings.json");

    public string ApiId { get; set; } = "";
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";

    public string LastExportFolder { get; set; } = "";

    public static AppSettingsStore Load()
    {
        try
        {
            if (File.Exists(PathJson))
            {
                var json = File.ReadAllText(PathJson);
                var s = JsonSerializer.Deserialize<AppSettingsStore>(json);
                if (s == null)
                    return new AppSettingsStore();
                return s;
            }
        }
        catch
        {
            /* ignore */
        }

        return new AppSettingsStore();
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(PathJson);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(PathJson, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
