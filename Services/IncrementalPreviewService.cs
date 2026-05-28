using System.IO;

namespace TelegaScan.Services;

public static class IncrementalPreviewService
{
    public static (int NewMessages, long LastId, bool HasPreviousExport) Preview(string outputDir, int totalMessages, long maxMessageId, bool incremental)
    {
        if (!incremental || !Directory.Exists(outputDir))
            return (totalMessages, 0, false);

        var state = IncrementalStateService.Load(outputDir);
        if (state.LastMessageId <= 0)
            return (totalMessages, 0, Directory.Exists(Path.Combine(outputDir, "index.html")) || Directory.Exists(Path.Combine(outputDir, "_index.html")));

        var newCount = totalMessages; // caller passes filtered count when available
        return (newCount, state.LastMessageId, true);
    }

    public static string FormatDiffSummary(int newMessages, long lastId, bool hasExport)
    {
        if (!hasExport) return "первый экспорт";
        if (lastId <= 0) return "есть папка, индекс сообщений пуст";
        return newMessages > 0 ? $"~{newMessages} новых (после id {lastId})" : "новых сообщений нет";
    }
}
