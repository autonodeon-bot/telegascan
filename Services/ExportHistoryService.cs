using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegaScan.Services;

public sealed class ExportHistoryEntry
{
    public DateTime Date { get; init; }
    public string ChatTitle { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public ExportMode Mode { get; init; }
    public int MessageCount { get; init; }
    public int MediaCount { get; init; }
    public long TotalBytes { get; init; }
    public bool Incremental { get; init; }
    public TimeSpan Duration { get; init; }
}

public static class ExportHistoryService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string HistoryPath
    {
        get
        {
            // Портативный режим: если рядом с exe есть папка writable — используем её
            var exeDir = AppContext.BaseDirectory;
            var portableFile = Path.Combine(exeDir, "export_history.json");
            try
            {
                if (Directory.Exists(exeDir) &&
                    !portableFile.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
                    return portableFile;
            }
            catch { /* */ }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TelegaScan",
                "export_history.json");
        }
    }

    public static List<ExportHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new List<ExportHistoryEntry>();
            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<List<ExportHistoryEntry>>(json, Opts) ?? new List<ExportHistoryEntry>();
        }
        catch { return new List<ExportHistoryEntry>(); }
    }

    public static void Append(ExportHistoryEntry entry)
    {
        var list = Load();
        list.Insert(0, entry);
        if (list.Count > 200) list = list.Take(200).ToList();
        var dir = Path.GetDirectoryName(HistoryPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(list, Opts));
    }
}
