using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace TelegaScan.Services;

/// <summary>
/// Экспорт Telegram-канала в структуру год/месяц/дата-тема.
/// Каждый день — отдельная папка с медиа, текстом и ссылками.
/// Корневой _index.html — навигация по дереву + поиск.
/// </summary>
public static class ChannelExportService
{
    // ─── Public entry point ───────────────────────────────────────────────────

    public static async Task ExportChannelAsync(
        Client client, InputPeer peer, string channelTitle,
        string outputRoot, ExportOptions options,
        IProgress<ExportProgressReport>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(outputRoot);
        var emit = progress is null ? null : new ChatExportService.ExportProgressEmitter(progress);

        emit?.SetPhase(ExportWorkPhase.History);
        emit?.Log("Канал: загрузка истории сообщений…");
        var (sorted, _, _) = await ChatExportService.LoadAllMessagesInternalAsync(
            client, peer, options, emit, ct).ConfigureAwait(false);

        emit?.MessagesLoadedCount = sorted.Count;
        emit?.Log($"Всего сообщений: {sorted.Count}. Группировка по дням…");

        // Группируем по датам (местное время)
        var days = sorted
            .OfType<Message>()
            .GroupBy(m => m.Date.ToLocalTime().Date)
            .OrderBy(g => g.Key)
            .ToList();

        emit?.Log($"Уникальных дней: {days.Count}. Подготовка структуры папок…");

        var dayEntries = new List<DayEntry>(days.Count);

        emit?.SetPhase(ExportWorkPhase.Media);
        var totalDays = days.Count;
        var donedays = 0;

        foreach (var dayGroup in days)
        {
            ct.ThrowIfCancellationRequested();
            var date = dayGroup.Key;
            var msgs = dayGroup.OrderBy(m => m.id).ToList();

            var topic = ExtractDayTopic(msgs);
            var slug = MakeSlug(topic);
            var folderName = $"{date:dd}_{slug}";

            var yearDir = Path.Combine(outputRoot, date.ToString("yyyy"));
            var monthDir = Path.Combine(yearDir, date.ToString("MM"));
            var dayDir = Path.Combine(monthDir, folderName);
            var dayMediaDir = Path.Combine(dayDir, "media");
            Directory.CreateDirectory(dayMediaDir);

            // Скачиваем медиа дня
            var (mediaCount, videoCount, photoCount, docCount) =
                await DownloadDayMediaAsync(client, peer, msgs, dayMediaDir, options, emit, ct)
                    .ConfigureAwait(false);

            // Пишем content.txt
            await WriteContentFileAsync(Path.Combine(dayDir, "content.txt"), channelTitle, date, msgs, ct)
                .ConfigureAwait(false);

            // Пишем links.txt
            var links = ExtractLinks(msgs);
            if (links.Count > 0)
                await WriteLinksFileAsync(Path.Combine(dayDir, "links.txt"), date, links, ct)
                    .ConfigureAwait(false);

            // Пишем день index.html
            await WriteDayHtmlAsync(Path.Combine(dayDir, "index.html"), channelTitle, date, topic, msgs,
                dayMediaDir, ct).ConfigureAwait(false);

            donedays++;
            emit?.SyncMedia(new ChatExportService.MediaDownloadStats { TotalBytes = mediaCount }, donedays, totalDays,
                $"{date:dd.MM.yyyy} — {topic}");

            dayEntries.Add(new DayEntry
            {
                Date = date,
                Topic = topic,
                RelPath = Path.Combine(date.ToString("yyyy"), date.ToString("MM"), folderName).Replace('\\', '/'),
                MediaCount = mediaCount,
                VideoCount = videoCount,
                PhotoCount = photoCount,
                DocCount = docCount,
                HasLinks = links.Count > 0,
                MessageCount = msgs.Count
            });
        }

        emit?.SetPhase(ExportWorkPhase.Html);
        emit?.Log("Генерация навигационного _index.html…");
        await WriteNavIndexAsync(Path.Combine(outputRoot, "_index.html"), channelTitle, dayEntries, ct)
            .ConfigureAwait(false);

        emit?.SetPhase(ExportWorkPhase.Done);
        emit?.CurrentFileHint = null;
        emit?.Log($"Канал экспортирован → {outputRoot}");
        emit?.Log($"Навигация: {outputRoot}\\_index.html");
    }

    // ─── Topic extraction ─────────────────────────────────────────────────────

    private static string ExtractDayTopic(List<Message> msgs)
    {
        // Приоритет: первый пост с текстом длиннее 10 символов
        foreach (var m in msgs)
        {
            if (!string.IsNullOrWhiteSpace(m.message) && m.message.Length > 10)
            {
                var text = m.message.Replace('\n', ' ').Trim();
                // Берём первые 70 символов, обрезаем по последнему слову
                if (text.Length > 70)
                {
                    var cut = text.LastIndexOf(' ', 70);
                    text = cut > 20 ? text[..cut] + "…" : text[..70] + "…";
                }
                return text;
            }
        }

        // Нет текста — берём имя первого видеофайла
        foreach (var m in msgs)
        {
            var doc = ChatExportService.GuessPrimaryDocumentInternal(m);
            if (doc != null && ChatExportService.IsVideoDocument(doc))
            {
                var fn = doc.Filename;
                if (!string.IsNullOrWhiteSpace(fn))
                    return Path.GetFileNameWithoutExtension(fn);
            }
        }

        return "Без названия";
    }

    private static string MakeSlug(string topic)
    {
        // Только буквы/цифры/пробелы → заменяем пробелы дефисом, убираем остальное
        var s = Regex.Replace(topic, @"[^\w\s\u0400-\u04FF-]", "");
        s = Regex.Replace(s, @"\s+", "-");
        s = s.Trim('-');
        if (s.Length > 50) s = s[..50].TrimEnd('-');
        return string.IsNullOrEmpty(s) ? "post" : s;
    }

    // ─── Links extraction ─────────────────────────────────────────────────────

    private static List<string> ExtractLinks(List<Message> msgs)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlRx = new Regex(@"https?://[^\s\)\]>""]+", RegexOptions.IgnoreCase);
        foreach (var m in msgs)
        {
            if (!string.IsNullOrEmpty(m.message))
                foreach (Match match in urlRx.Matches(m.message))
                    links.Add(match.Value.TrimEnd('.', ',', '!', '?', ';'));

            if (m.entities is not null)
                foreach (var e in m.entities)
                    if (e is MessageEntityUrl mu)
                    {
                        var url = m.message?[mu.offset..(mu.offset + mu.length)];
                        if (!string.IsNullOrEmpty(url)) links.Add(url);
                    }
                    else if (e is MessageEntityTextUrl mtu && !string.IsNullOrEmpty(mtu.url))
                        links.Add(mtu.url);
        }
        return links.ToList();
    }

    // ─── Media download ───────────────────────────────────────────────────────

    private static async Task<(int total, int videos, int photos, int docs)> DownloadDayMediaAsync(
        Client client, InputPeer peer, List<Message> msgs, string mediaDir,
        ExportOptions opts, ChatExportService.ExportProgressEmitter? emit, CancellationToken ct)
    {
        if (opts.Quality == MediaQuality.None)
            return (0, 0, 0, 0);

        var st = new ChatExportService.MediaDownloadStats();

        foreach (var msg0 in msgs)
        {
            ct.ThrowIfCancellationRequested();
            if (msg0.media is null) continue;

            try
            {
                var msg = await ChatExportService.EnsureMessageInternalAsync(client, peer, msg0, opts, emit, ct)
                    .ConfigureAwait(false);

                switch (msg.media)
                {
                    case MessageMediaPhoto { photo: Photo photo }:
                    {
                        var fp = Path.Combine(mediaDir, $"photo_{msg.id}.jpg");
                        if (File.Exists(fp) && new FileInfo(fp).Length > 0) { st.Skipped++; break; }
                        PhotoSizeBase? targetSize = opts.Quality switch
                        {
                            MediaQuality.Thumbs => PickSmallPhotoSize(photo),
                            MediaQuality.Compressed => PickMedPhotoSize(photo),
                            _ => null
                        };
                        emit?.Log($"  фото {msg.id}…");
                        await RateLimitedTelegram.ExecuteAsync(async () =>
                        {
                            await using var fs = File.Create(fp);
                            await client.DownloadFileAsync(photo, fs, targetSize).ConfigureAwait(false);
                        }, opts.DelayAfterEachMediaFile, ct).ConfigureAwait(false);
                        var plen = new FileInfo(fp).Length;
                        if (plen == 0)
                        {
                            try { File.Delete(fp); } catch { /* */ }
                            await Task.Delay(3000, ct).ConfigureAwait(false);
                            await RateLimitedTelegram.ExecuteAsync(async () =>
                            {
                                await using var fs = File.Create(fp);
                                await client.DownloadFileAsync(photo, fs).ConfigureAwait(false);
                            }, opts.DelayAfterEachMediaFile, ct).ConfigureAwait(false);
                        }
                        st.Photos++;
                        st.TotalBytes += new FileInfo(fp).Length;
                        break;
                    }
                    case MessageMediaDocument mmd:
                    {
                        if (opts.Quality == MediaQuality.Thumbs) break;
                        // Если документ не загружен — пробуем обновить через повторный запрос
                        var doc = ChatExportService.PickDocumentInternal(mmd);
                        if (doc is null)
                        {
                            emit?.Log($"  документ #{msg.id}: не загружен, пропуск");
                            break;
                        }
                        emit?.Log($"  {Path.GetFileName(doc.Filename ?? "doc")} ({msg.id})…");
                        await ChatExportService.DownloadDocumentInternalAsync(
                            client, peer, msg.id, doc, mediaDir, opts, emit, ct, st).ConfigureAwait(false);
                        break;
                    }
                    case MessageMediaWebPage { webpage: WebPage wp }:
                    {
                        if (wp.document is not Document wdoc) break;
                        if (opts.Quality == MediaQuality.Thumbs) break;
                        await ChatExportService.DownloadDocumentInternalAsync(
                            client, peer, msg.id, wdoc, mediaDir, opts, emit, ct, st).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                emit?.Log($"  медиа #{msg0.id}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return (st.Photos + st.Videos + st.Gifs + st.Stickers + st.Voices + st.Docs,
                st.Videos, st.Photos, st.Docs);
    }

    private static PhotoSizeBase? PickSmallPhotoSize(Photo photo)
        => photo.sizes?.OrderBy(s => s switch
        {
            PhotoSize ps => (long)ps.w * ps.h,
            PhotoSizeProgressive ps => (long)ps.w * ps.h,
            _ => long.MaxValue
        }).FirstOrDefault();

    private static PhotoSizeBase? PickMedPhotoSize(Photo photo)
    {
        if (photo.sizes is null || photo.sizes.Length == 0) return null;
        var ordered = photo.sizes
            .OrderBy(s => s switch
            {
                PhotoSize ps => (long)ps.w * ps.h,
                PhotoSizeProgressive ps => (long)ps.w * ps.h,
                _ => long.MaxValue
            }).ToArray();
        return ordered.Length > 1 ? ordered[ordered.Length / 2] : ordered[0];
    }

    // ─── File writers ─────────────────────────────────────────────────────────

    private static async Task WriteContentFileAsync(
        string path, string channelTitle, DateTime date, List<Message> msgs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {channelTitle} — {date:dd MMMM yyyy} ===");
        sb.AppendLine();
        foreach (var m in msgs)
        {
            var time = m.Date.ToLocalTime().ToString("HH:mm");
            if (!string.IsNullOrWhiteSpace(m.message))
            {
                sb.AppendLine($"[{time}]");
                sb.AppendLine(m.message);
                sb.AppendLine();
            }
            else if (m.media is not null)
            {
                var desc = m.media switch
                {
                    MessageMediaPhoto => "[ФОТО]",
                    MessageMediaDocument mmd when mmd.document is Document d =>
                        ChatExportService.IsVideoDocument(d)
                            ? $"[ВИДЕО: {d.Filename ?? "video"}]"
                            : $"[ФАЙЛ: {d.Filename ?? "document"}]",
                    _ => "[МЕДИА]"
                };
                sb.AppendLine($"[{time}] {desc}");
                sb.AppendLine();
            }
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static async Task WriteLinksFileAsync(
        string path, DateTime date, List<string> links, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Ссылки за {date:dd.MM.yyyy} ===");
        sb.AppendLine();
        foreach (var l in links)
            sb.AppendLine(l);
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    // ─── Day HTML ─────────────────────────────────────────────────────────────

    private static async Task WriteDayHtmlAsync(
        string path, string channelTitle, DateTime date, string topic,
        List<Message> msgs, string mediaDir, CancellationToken ct)
    {
        var enc = WebUtility.HtmlEncode(channelTitle);
        var topicEnc = WebUtility.HtmlEncode(topic);
        var dateStr = date.ToString("dd.MM.yyyy");
        var dateLong = date.ToString("dd MMMM yyyy");
        var sb = new StringBuilder();
        sb.Append($$"""
<!DOCTYPE html><html lang="ru"><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{enc}} — {{dateStr}}</title>
<style>
:root{--bg:#0e1621;--pn:#17212b;--ac:#5288c1;--tx:#e4edf5;--mt:#7d8b99;--bd:#243b53}
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:"Segoe UI Variable","Segoe UI",system-ui,sans-serif;background:var(--bg);color:var(--tx);padding:24px 16px 60px;max-width:900px;margin:0 auto}
h1{font-size:1.4rem;font-weight:700;margin-bottom:4px}
.sub{color:var(--mt);font-size:.9rem;margin-bottom:24px}
.back{color:var(--ac);text-decoration:none;font-size:.9rem;display:inline-block;margin-bottom:16px}
.back:hover{text-decoration:underline}
.msg{background:var(--pn);border:1px solid var(--bd);border-radius:12px;padding:12px 16px;margin-bottom:12px}
.msg-time{color:var(--mt);font-size:.8rem;margin-bottom:6px}
.msg-text{white-space:pre-wrap;word-break:break-word;line-height:1.6}
.msg-text a{color:var(--ac)}
.media-wrap{margin-top:10px}
.media-wrap img{max-width:100%;border-radius:8px;display:block}
.media-wrap video{max-width:100%;border-radius:8px;display:block}
.doc-link{display:inline-flex;align-items:center;gap:8px;padding:8px 14px;background:#1a2836;border-radius:8px;color:var(--ac);text-decoration:none;font-size:.9rem}
.doc-link:hover{background:#1f3347}
</style></head><body>
<a href="../../../_index.html" class="back">← Навигация</a>
<h1>{{topicEnc}}</h1>
<div class="sub">{{enc}} · {{dateLong}}</div>

""");

        foreach (var m in msgs)
        {
            var time = m.Date.ToLocalTime().ToString("HH:mm");
            sb.Append($"<div class=\"msg\"><div class=\"msg-time\">{time}</div>");

            if (!string.IsNullOrWhiteSpace(m.message))
            {
                var msgHtml = FormatMessageHtml(m);
                sb.Append($"<div class=\"msg-text\">{msgHtml}</div>");
            }

            var mediaHtml = BuildMediaHtml(m, mediaDir);
            if (!string.IsNullOrEmpty(mediaHtml))
                sb.Append($"<div class=\"media-wrap\">{mediaHtml}</div>");

            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static string FormatMessageHtml(Message m)
    {
        if (string.IsNullOrEmpty(m.message)) return "";
        var encoded = WebUtility.HtmlEncode(m.message);

        // Восстанавливаем кликабельные ссылки из entity
        if (m.entities is null) return encoded;

        // Простое оборачивание URL entity обратно в <a> теги (работаем с encoded)
        // Для простоты — делаем post-process через regex
        encoded = Regex.Replace(encoded, @"https?://[^\s\&lt;\&quot;]+",
            m => $"<a href=\"{m.Value}\" target=\"_blank\" rel=\"noopener\">{m.Value}</a>");
        return encoded;
    }

    private static string BuildMediaHtml(Message m, string mediaDirPath)
    {
        switch (m.media)
        {
            case MessageMediaPhoto { photo: Photo }:
            {
                var fn = $"photo_{m.id}.jpg";
                var fp = Path.Combine(mediaDirPath, fn);
                if (!File.Exists(fp)) return "";
                return $"<img src=\"media/{fn}\" loading=\"lazy\" alt=\"фото\">";
            }
            case MessageMediaDocument mmd:
            {
                var doc = ChatExportService.PickDocumentInternal(mmd);
                if (doc is null) return "";
                var fn = ChatExportService.BuildDocumentFileNameInternal(m.id, doc);
                var fp = Path.Combine(mediaDirPath, fn);
                if (!File.Exists(fp)) return "";
                if (ChatExportService.IsVideoDocument(doc))
                    return $"<video src=\"media/{fn}\" controls preload=\"metadata\" style=\"max-height:480px\"></video>";
                if (doc.mime_type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                    return $"<img src=\"media/{fn}\" loading=\"lazy\" alt=\"изображение\">";
                return $"<a class=\"doc-link\" href=\"media/{fn}\" download>📎 {WebUtility.HtmlEncode(doc.Filename ?? fn)}</a>";
            }
            default:
                return "";
        }
    }

    // ─── Navigation HTML ──────────────────────────────────────────────────────

    private const string NavCss =
        ":root{--bg:#0e1621;--pn:#17212b;--ac:#5288c1;--tx:#e4edf5;--mt:#7d8b99;--bd:#243b53;--r:10px}" +
        "*{box-sizing:border-box;margin:0;padding:0}" +
        "body{font-family:\"Segoe UI Variable\",\"Segoe UI\",system-ui,sans-serif;background:var(--bg);color:var(--tx);min-height:100vh}" +
        ".header{background:var(--pn);border-bottom:1px solid var(--bd);padding:20px 28px;position:sticky;top:0;z-index:10}" +
        ".header h1{font-size:1.4rem;font-weight:700}" +
        ".header .sub{color:var(--mt);font-size:.875rem;margin-top:2px}" +
        ".search-wrap{margin-top:14px}" +
        "#search{width:100%;padding:10px 14px;background:#141e28;border:1px solid var(--bd);border-radius:var(--r);color:var(--tx);font-size:.95rem;outline:none}" +
        "#search:focus{border-color:var(--ac)}" +
        ".content{max-width:900px;margin:0 auto;padding:24px 20px 60px}" +
        "details{margin-bottom:8px}" +
        "details[open]>summary{border-radius:var(--r) var(--r) 0 0}" +
        "summary{list-style:none;cursor:pointer;padding:12px 16px;background:#17212b;border:1px solid var(--bd);border-radius:var(--r);font-weight:600;display:flex;align-items:center;gap:10px;user-select:none}" +
        "summary::-webkit-details-marker{display:none}" +
        "summary::before{content:'\\25B6';font-size:.75rem;color:var(--ac);transition:.2s;flex-shrink:0}" +
        "details[open]>summary::before{transform:rotate(90deg)}" +
        "summary:hover{background:#1a2836}" +
        ".year-title{font-size:1.05rem;color:var(--tx)}" +
        ".month-title{font-size:.95rem;color:var(--tx)}" +
        ".badge{margin-left:auto;font-size:.75rem;color:var(--mt);font-weight:400}" +
        ".month-body{border:1px solid var(--bd);border-top:none;border-radius:0 0 var(--r) var(--r);padding:8px 0;background:#121a24}" +
        ".day-row{display:flex;align-items:center;gap:12px;padding:8px 16px;text-decoration:none;color:var(--tx);border-bottom:1px solid #1e2d3d;transition:background .15s}" +
        ".day-row:last-child{border-bottom:none}" +
        ".day-row:hover{background:#1a2836}" +
        ".day-date{font-size:.85rem;font-weight:600;color:var(--ac);min-width:80px;flex-shrink:0}" +
        ".day-topic{flex:1;font-size:.875rem;color:var(--tx);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
        ".day-stats{font-size:.75rem;color:var(--mt);flex-shrink:0;text-align:right}" +
        "#results{display:none}" +
        "#results .day-row{border-radius:var(--r);margin-bottom:4px;border:1px solid var(--bd)}" +
        ".no-results{color:var(--mt);padding:24px;text-align:center}" +
        ".hl{background:rgba(82,136,193,.3);border-radius:3px;padding:0 2px}";

    private const string NavJs =
        "function doSearch(){" +
        "var q=document.getElementById('search').value.trim().toLowerCase();" +
        "var tree=document.getElementById('tree');" +
        "var res=document.getElementById('results');" +
        "if(!q){tree.style.display='';res.style.display='none';res.innerHTML='';return;}" +
        "tree.style.display='none';res.style.display='';" +
        "var hits=ENTRIES.filter(function(e){return e.topic.toLowerCase().indexOf(q)>=0||e.date.indexOf(q)>=0||e.dateAlt.indexOf(q)>=0;});" +
        "if(!hits.length){res.innerHTML='<div class=\"no-results\">\u041d\u0438\u0447\u0435\u0433\u043e \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d\u043e</div>';return;}" +
        "function esc(s){return s.replace(/[.*+?^${}()|[\\]\\\\]/g,'\\\\$&');}" +
        "res.innerHTML=hits.map(function(e){" +
        "function hl(t){return t.replace(new RegExp(esc(q),'gi'),function(m){return '<span class=\"hl\">'+m+'</span>';});}" +
        "return '<a class=\"day-row\" href=\"'+e.path+'index.html\">'+" +
        "'<span class=\"day-date\">'+hl(e.date)+'</span>'+" +
        "'<span class=\"day-topic\">'+hl(e.topic)+'</span>'+" +
        "'<span class=\"day-stats\">'+e.stats+'</span></a>';}).join('');}" +
        "document.getElementById('search').addEventListener('keydown',function(ev){" +
        "if(ev.key==='Escape'){ev.target.value='';doSearch();}});";

    private static async Task WriteNavIndexAsync(
        string path, string channelTitle, List<DayEntry> entries, CancellationToken ct)
    {
        var enc = WebUtility.HtmlEncode(channelTitle);
        var jsData = BuildJsData(entries);
        var treeHtml = BuildTreeHtml(entries);

        var sb = new StringBuilder(65536);
        sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>{enc} \u2014 \u041d\u0430\u0432\u0438\u0433\u0430\u0446\u0438\u044f</title>");
        sb.AppendLine($"<style>{NavCss}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"  <h1>{enc}</h1>");
        sb.AppendLine($"  <div class=\"sub\">\u0410\u0440\u0445\u0438\u0432 \u043a\u0430\u043d\u0430\u043b\u0430 &middot; {entries.Count} \u0434\u043d\u0435\u0439 &middot; {entries.Sum(e => e.MediaCount)} \u043c\u0435\u0434\u0438\u0430\u0444\u0430\u0439\u043b\u043e\u0432</div>");
        sb.AppendLine("  <div class=\"search-wrap\">");
        sb.AppendLine("    <input id=\"search\" type=\"text\" placeholder=\"\u041f\u043e\u0438\u0441\u043a \u043f\u043e \u0442\u0435\u043c\u0435, \u0434\u0430\u0442\u0435 (\u0434\u0434.\u043c\u043c.\u0433\u0433\u0433\u0433)\u2026\" oninput=\"doSearch()\" autocomplete=\"off\">");
        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"content\">");
        sb.AppendLine("  <div id=\"tree\">");
        sb.Append(treeHtml);
        sb.AppendLine("  </div>");
        sb.AppendLine("  <div id=\"results\"></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.AppendLine($"var ENTRIES={jsData};");
        sb.AppendLine(NavJs);
        sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static string BuildTreeHtml(List<DayEntry> entries)
    {
        var sb = new StringBuilder();
        var byYear = entries.GroupBy(e => e.Date.Year).OrderBy(g => g.Key);

        foreach (var yearGrp in byYear)
        {
            var yearMedia = yearGrp.Sum(e => e.MediaCount);
            sb.AppendLine($"    <details open>");
            sb.AppendLine($"      <summary><span class=\"year-title\">{yearGrp.Key}</span><span class=\"badge\">{yearGrp.Count()} дней · {yearMedia} медиа</span></summary>");
            sb.AppendLine($"      <div style=\"padding:4px 0 8px 16px\">");

            var byMonth = yearGrp.GroupBy(e => e.Date.Month).OrderBy(g => g.Key);
            foreach (var monthGrp in byMonth)
            {
                var monthName = new DateTime(yearGrp.Key, monthGrp.Key, 1).ToString("MMMM");
                var monthMedia = monthGrp.Sum(e => e.MediaCount);
                sb.AppendLine($"        <details>");
                sb.AppendLine($"          <summary><span class=\"month-title\">{monthName}</span><span class=\"badge\">{monthGrp.Count()} дней · {monthMedia} медиа</span></summary>");
                sb.AppendLine($"          <div class=\"month-body\">");

                foreach (var entry in monthGrp.OrderBy(e => e.Date))
                {
                    var statsTokens = new List<string>();
                    if (entry.PhotoCount > 0) statsTokens.Add($"📷{entry.PhotoCount}");
                    if (entry.VideoCount > 0) statsTokens.Add($"🎬{entry.VideoCount}");
                    if (entry.DocCount > 0) statsTokens.Add($"📎{entry.DocCount}");
                    if (entry.HasLinks) statsTokens.Add("🔗");
                    var stats = statsTokens.Count > 0 ? string.Join(" ", statsTokens) : "";

                    var topicEnc = WebUtility.HtmlEncode(entry.Topic.Length > 80
                        ? entry.Topic[..80] + "…" : entry.Topic);

                    var dateStr2 = entry.Date.ToString("dd.MM.yyyy");
                    var statsEnc = WebUtility.HtmlEncode(stats);
                    sb.AppendLine($"            <a class=\"day-row\" href=\"{entry.RelPath}/index.html\">");
                    sb.AppendLine($"              <span class=\"day-date\">{dateStr2}</span>");
                    sb.AppendLine($"              <span class=\"day-topic\">{topicEnc}</span>");
                    sb.AppendLine($"              <span class=\"day-stats\">{statsEnc}</span>");
                    sb.AppendLine("            </a>");
                }

                sb.AppendLine($"          </div>");
                sb.AppendLine($"        </details>");
            }

            sb.AppendLine($"      </div>");
            sb.AppendLine($"    </details>");
        }

        return sb.ToString();
    }

    private static string BuildJsData(List<DayEntry> entries)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var e in entries)
        {
            if (!first) sb.Append(',');
            first = false;
            var statsTokens = new List<string>();
            if (e.PhotoCount > 0) statsTokens.Add($"📷{e.PhotoCount}");
            if (e.VideoCount > 0) statsTokens.Add($"🎬{e.VideoCount}");
            if (e.DocCount > 0) statsTokens.Add($"📎{e.DocCount}");
            if (e.HasLinks) statsTokens.Add("🔗");
            var stats = string.Join(" ", statsTokens);

            var topic = e.Topic.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
            sb.Append($"{{\"date\":\"{e.Date:dd.MM.yyyy}\",\"dateAlt\":\"{e.Date:yyyy-MM-dd}\",\"topic\":\"{topic}\",\"path\":\"{e.RelPath}/\",\"stats\":\"{stats}\"}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ─── Helper types ─────────────────────────────────────────────────────────

    private sealed class DayEntry
    {
        public DateTime Date { get; init; }
        public string Topic { get; init; } = "";
        public string RelPath { get; init; } = "";
        public int MediaCount { get; init; }
        public int VideoCount { get; init; }
        public int PhotoCount { get; init; }
        public int DocCount { get; init; }
        public bool HasLinks { get; init; }
        public int MessageCount { get; init; }
    }

}
