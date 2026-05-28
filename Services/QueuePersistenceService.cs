using System.IO;
using System.Text.Json;

namespace TelegaScan.Services;

public sealed class PersistedQueueItem
{
    public string PeerKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public QueueItemStatus Status { get; set; }
    public int Position { get; set; }
}

/// <summary>Сохранение очереди скачивания между сеансами приложения.</summary>
public static class QueuePersistenceService
{
    private static string PathFor(string accountKey)
    {
        var safe = string.Join("_", accountKey.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe)) safe = "default";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegaScan",
            $"queue_{safe}.json");
    }

    public static IReadOnlyList<PersistedQueueItem> Load(string accountKey)
    {
        try
        {
            var path = PathFor(accountKey);
            if (!File.Exists(path)) return Array.Empty<PersistedQueueItem>();
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<PersistedQueueItem>>(json);
            return list is { Count: > 0 } ? list : Array.Empty<PersistedQueueItem>();
        }
        catch
        {
            return Array.Empty<PersistedQueueItem>();
        }
    }

    public static bool Exists(string accountKey) => File.Exists(PathFor(accountKey));

    public static void Delete(string accountKey)
    {
        try
        {
            var path = PathFor(accountKey);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }

    public static void Save(string accountKey, IEnumerable<ExportQueueItem> queue)
    {
        try
        {
            var items = queue
                .Where(q => q.Status != QueueItemStatus.Cancelled)
                .Select(q => new PersistedQueueItem
                {
                    PeerKey = q.PeerKey,
                    Title = q.Title,
                    Subtitle = q.Subtitle,
                    Status = q.Status == QueueItemStatus.Running ? QueueItemStatus.Pending : q.Status,
                    Position = q.Position
                })
                .OrderBy(x => x.Position)
                .ToList();

            var path = PathFor(accountKey);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (items.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            /* не мешаем закрытию приложения */
        }
    }
}
