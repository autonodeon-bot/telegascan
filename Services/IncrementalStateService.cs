using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegaScan.Services;

/// <summary>Состояние инкрементального экспорта, хранимое в папке экспорта.</summary>
public sealed class IncrementalState
{
    public int LastMessageId { get; set; }
    public DateTime LastExportDate { get; set; }
    public long TotalMediaBytes { get; set; }
    public int TotalMessages { get; set; }

    /// <summary>MD5-хеш → относительный путь файла (для дедупликации).</summary>
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class IncrementalStateService
{
    private const string StateFileName = "_state.json";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetStatePath(string outputRoot) =>
        Path.Combine(outputRoot, StateFileName);

    public static IncrementalState Load(string outputRoot)
    {
        var path = GetStatePath(outputRoot);
        if (!File.Exists(path)) return new IncrementalState();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IncrementalState>(json, Opts) ?? new IncrementalState();
        }
        catch { return new IncrementalState(); }
    }

    public static void Save(string outputRoot, IncrementalState state)
    {
        Directory.CreateDirectory(outputRoot);
        File.WriteAllText(GetStatePath(outputRoot), JsonSerializer.Serialize(state, Opts));
    }

    /// <summary>Вычислить MD5 файла и вернуть hex-строку.</summary>
    public static string ComputeFileMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(filePath);
        var hash = md5.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Проверить файл на дедупликацию. Возвращает null если файл уникальный (и добавляет в индекс),
    /// или относительный путь дубликата.
    /// </summary>
    public static string? CheckAndRegisterDedup(IncrementalState state, string filePath, string relPath)
    {
        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0) return null;
        var hash = ComputeFileMd5(filePath);
        if (state.FileHashes.TryGetValue(hash, out var existingPath))
            return existingPath; // дубликат
        state.FileHashes[hash] = relPath;
        return null;
    }

    /// <summary>Режим проверки целостности: найти файлы с неверным хешем или отсутствующие.</summary>
    public static List<IntegrityIssue> CheckIntegrity(string outputRoot, IncrementalState state)
    {
        var issues = new List<IntegrityIssue>();
        foreach (var (hash, relPath) in state.FileHashes)
        {
            var fullPath = Path.Combine(outputRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                issues.Add(new IntegrityIssue { RelPath = relPath, Issue = "Файл отсутствует", Hash = hash });
                continue;
            }
            var actualHash = ComputeFileMd5(fullPath);
            if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase))
                issues.Add(new IntegrityIssue { RelPath = relPath, Issue = $"Хеш не совпадает (ожидался {hash}, найден {actualHash})", Hash = hash });
        }
        return issues;
    }
}

public sealed class IntegrityIssue
{
    public string RelPath { get; init; } = "";
    public string Issue { get; init; } = "";
    public string Hash { get; init; } = "";
}
