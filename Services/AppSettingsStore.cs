using System.IO;
using System.Text.Json;

namespace TelegaScan.Services;

public sealed class AppSettingsStore
{
    private static readonly string PathJson = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegaScan",
        "settings.json");

    public string ApiId { get; set; } = "";
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string LastExportFolder { get; set; } = "";

    /// <summary>Обратный порядок сортировки списка чатов (Я–А, старые первые и т.д.).</summary>
    public bool ChatSortDescending { get; set; }

    public bool EnableFileLog { get; set; } = true;
    public bool EnableToast { get; set; } = true;
    public int AutoRetryCount { get; set; } = 3;

    public bool SchedulerEnabled { get; set; }
    public string SchedulerTime { get; set; } = "03:00";
    public string? LastSchedulerRunDate { get; set; }

    public static AppSettingsStore Load()
    {
        try
        {
            if (File.Exists(PathJson))
            {
                var json = File.ReadAllText(PathJson);
                var s = JsonSerializer.Deserialize<AppSettingsStore>(json);
                if (s != null) return s;
            }
        }
        catch { /* ignore */ }

        return new AppSettingsStore();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(PathJson);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(PathJson, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
