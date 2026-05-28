using System.IO;

namespace TelegaScan.Services;

/// <summary>Проверка завершённости фаз экспорта (текст / медиа / вспомогательные файлы).</summary>
public static class ExportCompletionTracker
{
    public static bool IsMediaEnabled(ExportOptions opts) => opts.Quality != MediaQuality.None;

    public static bool IsAuxEnabled(ExportOptions opts) =>
        opts.SaveLongTexts
        || opts.ExportJson
        || opts.ExportSqlite
        || opts.GenerateStatistics
        || opts.CreateZip;

    public static bool IsTextComplete(string outputRoot, IncrementalState state) =>
        state.TextExportComplete || File.Exists(Path.Combine(outputRoot, "index.html"));

    public static bool IsMediaComplete(string outputRoot, IncrementalState state, ExportOptions opts) =>
        !IsMediaEnabled(opts) || state.MediaExportComplete;

    public static bool IsAuxComplete(IncrementalState state, ExportOptions opts) =>
        !IsAuxEnabled(opts) || state.AuxExportComplete;

    public static bool IsFullyComplete(string outputRoot, IncrementalState state, ExportOptions opts) =>
        IsTextComplete(outputRoot, state)
        && IsMediaComplete(outputRoot, state, opts)
        && IsAuxComplete(state, opts);

    public static string? FindExistingChatDir(string folderRoot, DialogListItem chat)
    {
        if (!Directory.Exists(folderRoot)) return null;
        var baseName = ExportPathHelper.SanitizeName(chat.Title);
        var direct = Path.Combine(folderRoot, baseName);
        if (Directory.Exists(direct) && File.Exists(Path.Combine(direct, "index.html")))
            return direct;

        var withId = Path.Combine(folderRoot, $"{baseName}_{ExportPathHelper.GetPeerId(chat.Peer)}");
        if (Directory.Exists(withId) && File.Exists(Path.Combine(withId, "index.html")))
            return withId;

        foreach (var dir in Directory.EnumerateDirectories(folderRoot))
        {
            if (!File.Exists(Path.Combine(dir, "index.html"))) continue;
            var name = Path.GetFileName(dir);
            if (name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }
}
