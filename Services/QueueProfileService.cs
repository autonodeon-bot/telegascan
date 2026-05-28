using System.IO;
using System.Text.Json;

namespace TelegaScan.Services;

public sealed class QueueProfileChat
{
    public string PeerKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

public sealed class QueueProfile
{
    public string Name { get; set; } = "";
    public List<QueueProfileChat> Chats { get; set; } = new();
}

public static class QueueProfileService
{
    private static string ProfilesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegaScan",
        "profiles");

    public static IReadOnlyList<string> ListProfileNames()
    {
        if (!Directory.Exists(ProfilesDir)) return Array.Empty<string>();
        return Directory.GetFiles(ProfilesDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static QueueProfile Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return new QueueProfile { Name = name };
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<QueueProfile>(json) ?? new QueueProfile { Name = name };
    }

    public static void Save(QueueProfile profile)
    {
        Directory.CreateDirectory(ProfilesDir);
        var path = GetPath(profile.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(string name)
    {
        var path = GetPath(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string GetPath(string name) =>
        Path.Combine(ProfilesDir, SanitizeFileName(name) + ".json");

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "profile" : name.Trim();
    }
}
