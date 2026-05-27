using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace TelegaScan.Services;

/// <summary>
/// Генерирует все вспомогательные файлы после экспорта:
/// manifest.json, media_index.csv, members.csv, messages.json, messages.db, _statistics.html.
/// </summary>
public static class ExportOutputService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Manifest ─────────────────────────────────────────────────────────────

    public static async Task WriteManifestAsync(
        string outputRoot, string chatTitle, ExportMode mode,
        int messageCount, IncrementalState state, CancellationToken ct)
    {
        var manifest = new
        {
            exportDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            chatTitle,
            mode = mode.ToString(),
            messageCount,
            lastMessageId = state.LastMessageId,
            totalMediaFiles = state.FileHashes.Count,
            totalMediaBytes = state.TotalMediaBytes,
            version = "2.0"
        };
        var path = Path.Combine(outputRoot, "manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonOpts), ct);
    }

    // ─── Media index CSV ─────────────────────────────────────────────────────

    public static async Task WriteMediaIndexCsvAsync(
        string outputRoot, CancellationToken ct)
    {
        var csvPath = Path.Combine(outputRoot, "media_index.csv");
        var sb = new StringBuilder();
        sb.AppendLine("\"Файл\",\"Тип\",\"Размер (байт)\",\"Дата изменения\",\"Папка\"");

        var mediaRoot = Path.Combine(outputRoot, "media");
        if (Directory.Exists(mediaRoot))
        {
            foreach (var f in Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories).OrderBy(x => x))
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(f);
                var relDir = Path.GetRelativePath(outputRoot, fi.DirectoryName ?? "").Replace('\\', '/');
                var ext = fi.Extension.ToLowerInvariant();
                var type = ext switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".webp" => "Фото",
                    ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => "Видео",
                    ".gif" => "GIF",
                    ".ogg" or ".oga" or ".mp3" or ".m4a" => "Аудио",
                    ".pdf" => "PDF",
                    _ => "Файл"
                };
                sb.AppendLine($"\"{fi.Name}\",\"{type}\",{fi.Length},\"{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}\",\"{relDir}\"");
            }
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8, ct);
    }

    // ─── Members CSV ─────────────────────────────────────────────────────────

    public static async Task WriteMembersCsvAsync(
        string outputRoot, IEnumerable<(long Id, string Name, string? Username, string? Bio, string? Phone, bool IsBot)> members,
        CancellationToken ct)
    {
        var path = Path.Combine(outputRoot, "members.csv");
        var sb = new StringBuilder();
        sb.AppendLine("\"ID\",\"Имя\",\"Username\",\"Bio\",\"Телефон\",\"Бот\"");
        foreach (var m in members)
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine($"{m.Id},\"{Esc(m.Name)}\",\"{Esc(m.Username)}\",\"{Esc(m.Bio)}\",\"{Esc(m.Phone)}\",{(m.IsBot ? "Да" : "Нет")}");
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
    }

    // ─── JSON messages export ─────────────────────────────────────────────────

    public static async Task WriteMessagesJsonAsync<T>(
        string outputRoot, IEnumerable<T> messages, CancellationToken ct)
    {
        var path = Path.Combine(outputRoot, "messages.json");
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, messages, JsonOpts, ct);
    }

    // ─── SQLite database ─────────────────────────────────────────────────────

    public static async Task WriteSqliteAsync(
        string outputRoot, IEnumerable<MessageRecord> messages,
        IEnumerable<MemberRecord> members, CancellationToken ct)
    {
        var dbPath = Path.Combine(outputRoot, "messages.db");
        var connStr = $"Data Source={dbPath}";

        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY,
                date TEXT NOT NULL,
                from_id INTEGER,
                author TEXT,
                text TEXT,
                media_type TEXT,
                media_path TEXT,
                reply_to INTEGER,
                fwd_from TEXT,
                group_id INTEGER,
                thread_id INTEGER,
                is_forwarded INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS members (
                id INTEGER PRIMARY KEY,
                name TEXT,
                username TEXT,
                bio TEXT,
                phone TEXT,
                is_bot INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS media_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER,
                file_path TEXT,
                file_size INTEGER,
                media_type TEXT,
                md5_hash TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_msg_date ON messages(date);
            CREATE INDEX IF NOT EXISTS idx_msg_author ON messages(author);
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(text, author, content='messages', content_rowid='id');
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Insert messages in transaction
        await using var tx = await conn.BeginTransactionAsync(ct);
        var insertMsg = conn.CreateCommand();
        insertMsg.Transaction = (SqliteTransaction)tx;
        insertMsg.CommandText = """
            INSERT OR REPLACE INTO messages (id,date,from_id,author,text,media_type,media_path,reply_to,fwd_from,group_id,thread_id,is_forwarded)
            VALUES (@id,@date,@fid,@auth,@txt,@mtype,@mpath,@rto,@fwd,@grp,@thr,@isf)
            """;
        var pId = insertMsg.Parameters.Add("@id", SqliteType.Integer);
        var pDate = insertMsg.Parameters.Add("@date", SqliteType.Text);
        var pFid = insertMsg.Parameters.Add("@fid", SqliteType.Integer);
        var pAuth = insertMsg.Parameters.Add("@auth", SqliteType.Text);
        var pTxt = insertMsg.Parameters.Add("@txt", SqliteType.Text);
        var pMtype = insertMsg.Parameters.Add("@mtype", SqliteType.Text);
        var pMpath = insertMsg.Parameters.Add("@mpath", SqliteType.Text);
        var pRto = insertMsg.Parameters.Add("@rto", SqliteType.Integer);
        var pFwd = insertMsg.Parameters.Add("@fwd", SqliteType.Text);
        var pGrp = insertMsg.Parameters.Add("@grp", SqliteType.Integer);
        var pThr = insertMsg.Parameters.Add("@thr", SqliteType.Integer);
        var pIsf = insertMsg.Parameters.Add("@isf", SqliteType.Integer);

        foreach (var m in messages)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = m.Id;
            pDate.Value = m.Date;
            pFid.Value = (object?)m.FromId ?? DBNull.Value;
            pAuth.Value = (object?)m.Author ?? DBNull.Value;
            pTxt.Value = (object?)m.Text ?? DBNull.Value;
            pMtype.Value = (object?)m.MediaType ?? DBNull.Value;
            pMpath.Value = (object?)m.MediaPath ?? DBNull.Value;
            pRto.Value = (object?)m.ReplyTo ?? DBNull.Value;
            pFwd.Value = (object?)m.FwdFrom ?? DBNull.Value;
            pGrp.Value = (object?)m.GroupId ?? DBNull.Value;
            pThr.Value = (object?)m.ThreadId ?? DBNull.Value;
            pIsf.Value = m.IsForwarded ? 1 : 0;
            await insertMsg.ExecuteNonQueryAsync(ct);
        }

        // Members
        var insertMem = conn.CreateCommand();
        insertMem.Transaction = (SqliteTransaction)tx;
        insertMem.CommandText = "INSERT OR REPLACE INTO members(id,name,username,bio,phone,is_bot) VALUES(@id,@n,@u,@b,@p,@bot)";
        var pmId = insertMem.Parameters.Add("@id", SqliteType.Integer);
        var pmN = insertMem.Parameters.Add("@n", SqliteType.Text);
        var pmU = insertMem.Parameters.Add("@u", SqliteType.Text);
        var pmB = insertMem.Parameters.Add("@b", SqliteType.Text);
        var pmP = insertMem.Parameters.Add("@p", SqliteType.Text);
        var pmBot = insertMem.Parameters.Add("@bot", SqliteType.Integer);

        foreach (var m in members)
        {
            ct.ThrowIfCancellationRequested();
            pmId.Value = m.Id;
            pmN.Value = (object?)m.Name ?? DBNull.Value;
            pmU.Value = (object?)m.Username ?? DBNull.Value;
            pmB.Value = (object?)m.Bio ?? DBNull.Value;
            pmP.Value = (object?)m.Phone ?? DBNull.Value;
            pmBot.Value = m.IsBot ? 1 : 0;
            await insertMem.ExecuteNonQueryAsync(ct);
        }

        // FTS index
        var ftsCmd = conn.CreateCommand();
        ftsCmd.Transaction = (SqliteTransaction)tx;
        ftsCmd.CommandText = "INSERT INTO messages_fts(rowid,text,author) SELECT id,COALESCE(text,''),COALESCE(author,'') FROM messages";
        await ftsCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    // ─── Statistics HTML ─────────────────────────────────────────────────────

    public static async Task WriteStatisticsHtmlAsync(
        string outputRoot, string chatTitle, IReadOnlyList<MessageRecord> messages, CancellationToken ct)
    {
        if (messages.Count == 0) return;

        // Aggregate data
        var byAuthor = messages
            .Where(m => !string.IsNullOrEmpty(m.Author))
            .GroupBy(m => m.Author!)
            .Select(g => (Author: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(15)
            .ToList();

        var byHour = messages
            .GroupBy(m => DateTime.TryParse(m.Date, out var d) ? d.Hour : -1)
            .Where(g => g.Key >= 0)
            .OrderBy(g => g.Key)
            .Select(g => (Hour: g.Key, Count: g.Count()))
            .ToList();

        var byDate = messages
            .Where(m => DateTime.TryParse(m.Date, out _))
            .GroupBy(m => DateTime.Parse(m.Date).Date)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Count: g.Count()))
            .ToList();

        var mediaStats = new[]
        {
            ("Фото", messages.Count(m => m.MediaType == "photo")),
            ("Видео", messages.Count(m => m.MediaType == "video")),
            ("Файлы", messages.Count(m => m.MediaType == "doc")),
            ("Текст", messages.Count(m => string.IsNullOrEmpty(m.MediaType) && !string.IsNullOrEmpty(m.Text)))
        };

        var enc = WebUtility.HtmlEncode(chatTitle);
        var sb = new StringBuilder(32768);
        sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head>");
        sb.AppendLine("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>{enc} — Статистика</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("*{box-sizing:border-box;margin:0;padding:0}body{font-family:'Segoe UI Variable','Segoe UI',system-ui,sans-serif;background:#0e1621;color:#e4edf5;padding:24px 16px 60px;max-width:960px;margin:0 auto}");
        sb.AppendLine("h1{font-size:1.5rem;font-weight:700;margin-bottom:4px}.sub{color:#7d8b99;font-size:.875rem;margin-bottom:32px}");
        sb.AppendLine("h2{font-size:1.1rem;font-weight:600;margin:28px 0 14px;color:#5288c1}");
        sb.AppendLine(".card{background:#17212b;border:1px solid #243b53;border-radius:12px;padding:20px;margin-bottom:20px}");
        sb.AppendLine(".bar-row{display:flex;align-items:center;gap:10px;margin-bottom:8px;font-size:.85rem}");
        sb.AppendLine(".bar-label{width:160px;flex-shrink:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#c5d3df}");
        sb.AppendLine(".bar-fill{height:18px;background:#5288c1;border-radius:4px;min-width:2px;transition:.3s}");
        sb.AppendLine(".bar-count{color:#7d8b99;flex-shrink:0;min-width:40px;text-align:right}");
        sb.AppendLine(".stat-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:12px}");
        sb.AppendLine(".stat-card{background:#121a24;border:1px solid #243b53;border-radius:10px;padding:14px;text-align:center}");
        sb.AppendLine(".stat-num{font-size:1.8rem;font-weight:700;color:#5288c1}.stat-lbl{font-size:.8rem;color:#7d8b99;margin-top:4px}");
        sb.AppendLine(".hour-bars{display:flex;align-items:flex-end;gap:3px;height:80px;padding-bottom:4px}");
        sb.AppendLine(".hour-bar{flex:1;background:#5288c1;border-radius:3px 3px 0 0;min-height:2px;position:relative}");
        sb.AppendLine(".hour-bar:hover::after{content:attr(data-tip);position:absolute;bottom:105%;left:50%;transform:translateX(-50%);background:#243b53;color:#e4edf5;padding:3px 8px;border-radius:6px;font-size:.75rem;white-space:nowrap;pointer-events:none}");
        sb.AppendLine(".hour-labels{display:flex;gap:3px;font-size:.65rem;color:#7d8b99;margin-top:4px}");
        sb.AppendLine(".hour-labels span{flex:1;text-align:center}");
        sb.AppendLine(".timeline{display:flex;align-items:flex-end;gap:1px;height:60px;overflow-x:auto}");
        sb.AppendLine(".tl-bar{flex-shrink:0;width:4px;background:#5288c1;border-radius:2px 2px 0 0;min-height:1px}");
        sb.AppendLine("a{color:#5288c1;text-decoration:none}.back{display:inline-block;margin-bottom:16px;font-size:.9rem}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<a class=\"back\" href=\"index.html\">← Назад к архиву</a>");
        sb.AppendLine($"<h1>{enc}</h1>");
        sb.AppendLine($"<div class=\"sub\">Статистика архива · сформировано {DateTime.Now:dd.MM.yyyy HH:mm}</div>");

        // Summary cards
        sb.AppendLine("<div class=\"stat-grid\">");
        sb.AppendLine($"<div class=\"stat-card\"><div class=\"stat-num\">{messages.Count:N0}</div><div class=\"stat-lbl\">Сообщений</div></div>");
        sb.AppendLine($"<div class=\"stat-card\"><div class=\"stat-num\">{byAuthor.Count}</div><div class=\"stat-lbl\">Участников</div></div>");
        if (byDate.Count > 0)
        {
            var span = (byDate[^1].Date - byDate[0].Date).Days + 1;
            sb.AppendLine($"<div class=\"stat-card\"><div class=\"stat-num\">{span:N0}</div><div class=\"stat-lbl\">Дней</div></div>");
            var avg = messages.Count / Math.Max(span, 1);
            sb.AppendLine($"<div class=\"stat-card\"><div class=\"stat-num\">{avg}</div><div class=\"stat-lbl\">Сообщ./день</div></div>");
        }
        foreach (var (label, count) in mediaStats)
            if (count > 0)
                sb.AppendLine($"<div class=\"stat-card\"><div class=\"stat-num\">{count:N0}</div><div class=\"stat-lbl\">{label}</div></div>");
        sb.AppendLine("</div>");

        // Top authors
        if (byAuthor.Count > 0)
        {
            sb.AppendLine("<h2>Топ участников</h2><div class=\"card\">");
            var maxA = byAuthor[0].Count;
            foreach (var (author, count) in byAuthor)
            {
                var pct = maxA > 0 ? (double)count / maxA * 100 : 0;
                sb.AppendLine($"<div class=\"bar-row\"><span class=\"bar-label\">{WebUtility.HtmlEncode(author)}</span><div class=\"bar-fill\" style=\"width:{pct:F1}%\"></div><span class=\"bar-count\">{count:N0}</span></div>");
            }
            sb.AppendLine("</div>");
        }

        // Activity by hour
        if (byHour.Count > 0)
        {
            sb.AppendLine("<h2>Активность по часам</h2><div class=\"card\">");
            sb.AppendLine("<div class=\"hour-bars\">");
            var maxH = byHour.Max(x => x.Count);
            for (var h = 0; h < 24; h++)
            {
                var cnt = byHour.FirstOrDefault(x => x.Hour == h).Count;
                var pct = maxH > 0 ? (double)cnt / maxH * 100 : 0;
                sb.AppendLine($"<div class=\"hour-bar\" style=\"height:{pct:F1}%\" data-tip=\"{h:00}:00 — {cnt} сообщ.\"></div>");
            }
            sb.AppendLine("</div><div class=\"hour-labels\">");
            for (var h = 0; h < 24; h += 2) sb.Append($"<span>{h:00}</span>");
            sb.AppendLine("</div></div>");
        }

        // Timeline
        if (byDate.Count > 1)
        {
            sb.AppendLine("<h2>Активность по дням</h2><div class=\"card\">");
            sb.AppendLine("<div class=\"timeline\">");
            var maxD = byDate.Max(x => x.Count);
            foreach (var (date, count) in byDate)
            {
                var pct = maxD > 0 ? (double)count / maxD * 100 : 0;
                sb.AppendLine($"<div class=\"tl-bar\" style=\"height:{pct:F1}%\" title=\"{date:dd.MM.yyyy}: {count}\"></div>");
            }
            sb.AppendLine("</div></div>");
        }

        sb.AppendLine("</body></html>");
        var path = Path.Combine(outputRoot, "_statistics.html");
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
    }

    // ─── ZIP output ─────────────────────────────────────────────────────────

    public static async Task CreateZipAsync(string outputRoot, CancellationToken ct)
    {
        var zipPath = outputRoot.TrimEnd('\\', '/') + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        await Task.Run(() => ZipFile.CreateFromDirectory(outputRoot, zipPath, CompressionLevel.Optimal, false), ct);
    }

    // ─── Long texts ──────────────────────────────────────────────────────────

    public static async Task WriteLongTextAsync(string outputRoot, int messageId, string text, DateTime date, string author, CancellationToken ct)
    {
        var dir = Path.Combine(outputRoot, "texts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"msg_{messageId}.txt");
        var content = $"ID: {messageId}\nДата: {date:dd.MM.yyyy HH:mm}\nАвтор: {author}\n\n{text}";
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string Esc(string? s) => s?.Replace("\"", "\"\"") ?? "";
}

// ─── Transfer types ───────────────────────────────────────────────────────────

public sealed class MessageRecord
{
    public int Id { get; init; }
    public string Date { get; init; } = "";
    public long? FromId { get; init; }
    public string? Author { get; init; }
    public string? Text { get; init; }
    public string? MediaType { get; init; }
    public string? MediaPath { get; init; }
    public int? ReplyTo { get; init; }
    public string? FwdFrom { get; init; }
    public long? GroupId { get; init; }
    public int? ThreadId { get; init; }
    public bool IsForwarded { get; init; }
}

public sealed class MemberRecord
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public string? Username { get; init; }
    public string? Bio { get; init; }
    public string? Phone { get; init; }
    public bool IsBot { get; init; }
}
