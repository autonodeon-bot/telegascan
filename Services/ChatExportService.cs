using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TL;
using WTelegram;

namespace TelegaScan.Services;

public enum MediaQuality { Original, Compressed, Thumbs, None }
public enum ExportMode { Chat, Channel }

public sealed class ExportOptions
{
    // ── Скорость ───────────────────────────────────────────────────
    public TimeSpan DelayBetweenHistoryRequests { get; init; } = TimeSpan.FromMilliseconds(900);
    public TimeSpan DelayAfterEachMediaFile { get; init; } = TimeSpan.FromMilliseconds(450);
    public int MessagesPerPage { get; init; } = 250;
    public int HistoryBatchSize { get; init; } = 100;
    public int ParallelDownloads { get; init; } = 2;

    // ── Режим и качество ───────────────────────────────────────────
    public MediaQuality Quality { get; init; } = MediaQuality.Original;
    public ExportMode Mode { get; init; } = ExportMode.Chat;
    public long CompressedMaxVideoBytes { get; init; } = 50L * 1024 * 1024;

    // ── Диапазон дат ───────────────────────────────────────────────
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }

    // ── Фильтры медиа ──────────────────────────────────────────────
    public bool DownloadPhotos { get; init; } = true;
    public bool DownloadVideos { get; init; } = true;
    public bool DownloadDocuments { get; init; } = true;
    public bool DownloadStickers { get; init; } = true;
    public bool DownloadVoice { get; init; } = true;

    // ── Инкрементальный режим ──────────────────────────────────────
    public bool Incremental { get; init; } = true;

    // ── Доп. форматы вывода ────────────────────────────────────────
    public bool ExportJson { get; init; } = true;
    public bool ExportSqlite { get; init; } = true;
    public bool GenerateStatistics { get; init; } = true;
    public bool CreateZip { get; init; } = false;

    // ── Структура хранения ─────────────────────────────────────────
    public bool SaveLongTexts { get; init; } = true;
    public int LongTextThreshold { get; init; } = 2000;
    public bool GroupAlbums { get; init; } = true;
    public bool GroupThreads { get; init; } = true;
    public bool TrackForwarded { get; init; } = true;
    public bool DeduplicateMedia { get; init; } = true;
}

public static class ChatExportService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // ─────────────────────── Dialog list ───────────────────────

    public static async Task<List<DialogListItem>> LoadDialogsAsync(Client client)
    {
        var dialogs = await client.Messages_GetAllDialogs().ConfigureAwait(false);
        var users = new Dictionary<long, User>();
        var chats = new Dictionary<long, ChatBase>();
        TL.Services.CollectUsersChats(dialogs, users, chats);

        var topMessages = new Dictionary<int, MessageBase>();
        foreach (var m in dialogs.Messages)
            topMessages[m.ID] = m;

        var list = new List<DialogListItem>();
        foreach (var db in dialogs.dialogs)
        {
            if (db is not Dialog dlg) continue;
            var uc = dialogs.UserOrChat(dlg.Peer);
            if (uc is null) continue;

            InputPeer? peer = uc switch
            {
                User u => new InputPeerUser(u.id, u.access_hash),
                Chat c => new InputPeerChat(c.id),
                Channel ch => new InputPeerChannel(ch.id, ch.access_hash),
                _ => null
            };
            if (peer is null) continue;

            var (title, subtitle, kind) = FormatDialogInfo(uc);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var isArchived = dlg.folder_id == 1;
            var lastUtc = DateTime.MinValue;
            if (dlg.top_message > 0 && topMessages.TryGetValue(dlg.top_message, out var topMb) && topMb is Message topMsg)
            {
                lastUtc = topMsg.Date.Kind == DateTimeKind.Utc
                    ? topMsg.Date
                    : topMsg.Date.ToUniversalTime();
            }

            list.Add(new DialogListItem
            {
                Title = title,
                Peer = peer,
                Subtitle = isArchived ? subtitle + " · Архив" : subtitle,
                Kind = kind,
                IsArchived = isArchived,
                PeerId = ExportPathHelper.GetPeerId(peer),
                LastMessageUtc = lastUtc
            });
        }

        return list;
    }

    /// <summary>Подгружает общее число сообщений для сортировки (фоновые запросы к API).</summary>
    public static async Task EnrichMessageCountsAsync(
        Client client,
        IEnumerable<DialogListItem> items,
        CancellationToken ct,
        Func<bool>? shouldStop = null)
    {
        var delay = TimeSpan.FromMilliseconds(400);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (shouldStop?.Invoke() == true) break;
            if (item.MessageCount >= 0) continue;
            try
            {
                var slice = await RateLimitedTelegram.ExecuteAsync(
                    () => client.Messages_GetHistory(item.Peer, 0, limit: 1),
                    delay, ct).ConfigureAwait(false);
                item.MessageCount = slice.Count;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                item.MessageCount = 0;
            }
        }
    }

    private static (string title, string sub, DialogKind kind) FormatDialogInfo(IPeerInfo uc) => uc switch
    {
        User u when u.IsBot => (UserName(u), $"Бот{Uname(u)}", DialogKind.Bot),
        User u => (UserName(u), $"Личный чат{Uname(u)}", DialogKind.Personal),
        Chat c => (c.title ?? "?", c.participants_count > 0 ? $"Группа · {c.participants_count} уч." : "Группа", DialogKind.Group),
        Channel ch when ch.flags.HasFlag(Channel.Flags.broadcast) =>
            (ch.title ?? "?", ch.participants_count > 0 ? $"Канал · {ch.participants_count} подп." : "Канал", DialogKind.Channel),
        Channel ch =>
            (ch.title ?? "?", ch.participants_count > 0 ? $"Группа · {ch.participants_count} уч." : "Группа", DialogKind.Supergroup),
        _ => (uc?.ToString() ?? "?", "", DialogKind.Personal)
    };

    private static string UserName(User u)
    {
        var name = $"{u.first_name} {u.last_name}".Trim();
        return string.IsNullOrEmpty(name) ? (u.username ?? $"id{u.id}") : name;
    }

    private static string Uname(User u) =>
        string.IsNullOrEmpty(u.username) ? "" : $" · @{u.username}";

    // ─────────────────────── Export ───────────────────────

    public static async Task ExportChatToHtmlAsync(
        Client client, InputPeer peer, string chatTitle,
        string outputRoot, ExportOptions options,
        IProgress<ExportProgressReport>? progress, CancellationToken ct)
    {
        if (options.Mode == ExportMode.Channel)
        {
            await ChannelExportService.ExportChannelAsync(client, peer, chatTitle, outputRoot, options, progress, ct);
            return;
        }

        var startedAt = DateTime.UtcNow;
        Directory.CreateDirectory(outputRoot);

        // ─ Инкрементальное состояние ─────────────────────────────
        var state = options.Incremental ? IncrementalStateService.Load(outputRoot) : new IncrementalState();
        var prevLastId = state.LastMessageId;
        var mediaEnabled = ExportCompletionTracker.IsMediaEnabled(options);
        var auxEnabled = ExportCompletionTracker.IsAuxEnabled(options);

        var emit = progress is null ? null : new ExportProgressEmitter(progress);
        emit?.ConfigureSegments(mediaEnabled, auxEnabled);
        emit?.SetPhase(ExportWorkPhase.History);
        if (options.Incremental && prevLastId > 0)
            emit?.Log($"Инкрементальный режим: продолжаем с сообщения #{prevLastId + 1}…");
        else
            emit?.Log("Загрузка истории сообщений…");

        var (allSorted, users, chats) = await LoadAllMessagesAsync(client, peer, options, emit, ct);

        // Фильтр по дате / инкремент
        var sorted = ApplyDateAndIncrementalFilter(allSorted, options, prevLastId);
        emit?.MessagesLoadedCount = sorted.Count;

        if (sorted.Count == 0 && prevLastId > 0
            && ExportCompletionTracker.IsFullyComplete(outputRoot, state, options))
        {
            emit?.Log("Нет новых сообщений. Экспорт полностью завершён.");
            emit?.SetPhase(ExportWorkPhase.Done);
            return;
        }

        if (sorted.Count == 0 && prevLastId > 0)
            emit?.Log("Нет новых сообщений в истории — докачка медиа и файлов…");

        // Для медиа нужны все сообщения, если текст уже выгружен ранее
        var mediaMessages = sorted.Count > 0
            ? sorted
            : ApplyDateAndIncrementalFilter(allSorted, options, lastId: 0);

        Dictionary<long, MemberData> members;
        var htmlPath = Path.Combine(outputRoot, "index.html");
        var skipText = ExportCompletionTracker.IsTextComplete(outputRoot, state) && sorted.Count == 0;

        if (!skipText)
        {
            emit?.SetPhase(ExportWorkPhase.Participants);
            emit?.Log($"Сообщений для обработки: {Math.Max(sorted.Count, mediaMessages.Count)}. Загрузка участников…");
            members = await LoadParticipantsAsync(client, peer, users, chats, options, emit, ct);

            emit?.SetPhase(ExportWorkPhase.Html);
            emit?.Log("Формирование HTML…");
            var textById = new Dictionary<int, string>();
            foreach (var m in sorted) textById[m.ID] = PlainPreview(m);

            var msgDataList = new List<MsgData>(sorted.Count);
            foreach (var m in sorted)
                msgDataList.Add(BuildMsgData(m, users, chats, textById, yearMonthSubdir: true));

            var authorMap = msgDataList.ToDictionary(d => d.Id, d => d.Author);
            foreach (var md in msgDataList)
            {
                if (md.ReplyTo is { } rid && authorMap.TryGetValue(rid, out var ra))
                    md.ReplyAuthor = ra;
            }

            await WriteHtmlAsync(htmlPath, chatTitle, allSorted.Count, msgDataList, members, options.MessagesPerPage, ct);
            state.TextExportComplete = true;
            emit?.MarkTextComplete();
            if (options.Incremental && sorted.Count > 0)
            {
                state.LastMessageId = sorted.Max(m => m.ID);
                state.LastExportDate = DateTime.UtcNow;
                state.TotalMessages += sorted.Count;
            }
            IncrementalStateService.Save(outputRoot, state);
        }
        else
        {
            emit?.Log("Текст и HTML уже готовы — пропуск.");
            emit?.MarkTextComplete();
            members = await LoadParticipantsAsync(client, peer, users, chats, options, emit, ct);
        }

        // ─ Скачивание медиа ──────────────────────────────────────
        if (mediaEnabled && !state.MediaExportComplete)
        {
            emit?.SetPhase(ExportWorkPhase.Media);
            emit?.Log("Скачивание медиа…");
            await DownloadAllMediaAsync(client, peer, mediaMessages, outputRoot, options, state, emit, ct);
            state.MediaExportComplete = true;
            IncrementalStateService.Save(outputRoot, state);
        }
        else if (!mediaEnabled)
        {
            state.MediaExportComplete = true;
            IncrementalStateService.Save(outputRoot, state);
        }
        else
        {
            emit?.Log("Медиа уже скачано — пропуск.");
            emit?.SyncMedia(new MediaDownloadStats(), 1, 1, null);
        }

        // ─ Вспомогательные файлы ─────────────────────────────────
        if (!state.AuxExportComplete)
        {
            var longTexts = options.SaveLongTexts
                ? mediaMessages.OfType<Message>()
                    .Where(m => m.message?.Length >= options.LongTextThreshold)
                    .ToList()
                : [];
            var auxSteps = CountAuxSteps(options, members.Count > 0) + longTexts.Count;
            emit?.BeginAuxPhase(auxSteps);

            if (longTexts.Count > 0)
            {
                emit?.Log($"Сохранение {longTexts.Count} длинных текстов в /texts/…");
                foreach (var m in longTexts)
                {
                    var author = ResolveAuthorName(m, users, chats);
                    await ExportOutputService.WriteLongTextAsync(outputRoot, m.id, m.message!, m.Date, author, ct);
                    emit?.AuxStep();
                }
            }

            emit?.Log("Формирование вспомогательных файлов…");

            if (options.ExportJson)
            {
                var records = BuildMessageRecords(mediaMessages, users, chats);
                await ExportOutputService.WriteMessagesJsonAsync(outputRoot, records, ct);
                emit?.Log("  → messages.json");
                emit?.AuxStep();
            }

            if (options.ExportSqlite)
            {
                var records = BuildMessageRecords(mediaMessages, users, chats);
                var memberRecords = members.Select(kv => new MemberRecord
                {
                    Id = kv.Key,
                    Name = kv.Value.Name,
                    Username = kv.Value.Username,
                    Bio = kv.Value.Bio,
                    Phone = kv.Value.Phone,
                    IsBot = kv.Value.IsBot
                });
                await ExportOutputService.WriteSqliteAsync(outputRoot, records, memberRecords, ct);
                emit?.Log("  → messages.db");
                emit?.AuxStep();
            }

            if (members.Count > 0)
            {
                var memTuples = members.Select(kv => (
                    kv.Key, kv.Value.Name, kv.Value.Username, kv.Value.Bio, kv.Value.Phone, kv.Value.IsBot));
                await ExportOutputService.WriteMembersCsvAsync(outputRoot, memTuples, ct);
                emit?.Log("  → members.csv");
                emit?.AuxStep();
            }

            await ExportOutputService.WriteMediaIndexCsvAsync(outputRoot, ct);
            emit?.Log("  → media_index.csv");
            emit?.AuxStep();

            if (options.GenerateStatistics)
            {
                var statRecords = BuildMessageRecords(mediaMessages, users, chats).ToList();
                await ExportOutputService.WriteStatisticsHtmlAsync(outputRoot, chatTitle, statRecords, ct);
                emit?.Log("  → _statistics.html");
                emit?.AuxStep();
            }

            await ExportOutputService.WriteManifestAsync(outputRoot, chatTitle, options.Mode, allSorted.Count, state, ct);
            emit?.Log("  → manifest.json");
            emit?.AuxStep();

            if (options.CreateZip)
            {
                emit?.Log("Создание ZIP-архива…");
                await ExportOutputService.CreateZipAsync(outputRoot, ct);
                emit?.Log($"  → {Path.GetFileName(outputRoot)}.zip");
                emit?.AuxStep();
            }

            state.AuxExportComplete = true;
            emit?.MarkAuxComplete();
            IncrementalStateService.Save(outputRoot, state);
        }
        else
        {
            emit?.Log("Вспомогательные файлы уже готовы — пропуск.");
            emit?.MarkAuxComplete();
        }

        if (sorted.Count > 0 && options.Incremental)
        {
            state.LastMessageId = sorted.Max(m => m.ID);
            state.LastExportDate = DateTime.UtcNow;
            IncrementalStateService.Save(outputRoot, state);
        }

        // ─ История экспортов ─────────────────────────────────────
        var duration = DateTime.UtcNow - startedAt;
        ExportHistoryService.Append(new ExportHistoryEntry
        {
            Date = DateTime.UtcNow,
            ChatTitle = chatTitle,
            OutputPath = outputRoot,
            Mode = options.Mode,
            MessageCount = sorted.Count,
            MediaCount = state.FileHashes.Count,
            TotalBytes = state.TotalMediaBytes,
            Incremental = options.Incremental && prevLastId > 0,
            Duration = duration
        });

        emit?.SetPhase(ExportWorkPhase.Done);
        emit?.CurrentFileHint = null;
        emit?.Log($"Готово за {duration.TotalSeconds:F0}с → {outputRoot}");
    }

    private static int CountAuxSteps(ExportOptions options, bool hasMembers)
    {
        var n = 2; // media_index.csv + manifest.json
        if (options.ExportJson) n++;
        if (options.ExportSqlite) n++;
        if (hasMembers) n++;
        if (options.GenerateStatistics) n++;
        if (options.CreateZip) n++;
        return n;
    }

    private static List<MessageBase> ApplyDateAndIncrementalFilter(
        List<MessageBase> all, ExportOptions opts, int lastId)
    {
        var result = all.AsEnumerable();

        // Инкрементальный: только сообщения новее lastId
        if (opts.Incremental && lastId > 0)
            result = result.Where(m => m.ID > lastId);

        // Диапазон дат
        if (opts.FromDate.HasValue)
        {
            var from = opts.FromDate.Value.Date;
            result = result.Where(m => m.Date.ToLocalTime().Date >= from);
        }
        if (opts.ToDate.HasValue)
        {
            var to = opts.ToDate.Value.Date;
            result = result.Where(m => m.Date.ToLocalTime().Date <= to);
        }

        return result.ToList();
    }

    private static IEnumerable<MessageRecord> BuildMessageRecords(
        List<MessageBase> sorted, Dictionary<long, User> users, Dictionary<long, ChatBase> chats)
    {
        foreach (var mb in sorted)
        {
            var msg = mb as Message;
            var fromId = ResolvePeerId(mb.From) ?? ResolvePeerId(mb.Peer) ?? 0;
            var author = ResolveAuthorName(mb, users, chats);
            var fwd = msg?.fwd_from is { from_name: { } fn } ? fn :
                      msg?.fwd_from?.from_id is PeerUser pu && users.TryGetValue(pu.user_id, out var fu) ? UserName(fu) : null;
            var threadId = (msg?.reply_to as MessageReplyHeader)?.reply_to_top_id;

            yield return new MessageRecord
            {
                Id = mb.ID,
                Date = mb.Date.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                FromId = fromId == 0 ? null : fromId,
                Author = author,
                Text = msg?.message,
                MediaType = msg != null ? GuessMediaType(msg) : null,
                MediaPath = msg != null ? GuessMediaPath(msg, yearMonthSubdir: true) : null,
                ReplyTo = (mb.ReplyTo as MessageReplyHeader)?.reply_to_msg_id,
                FwdFrom = fwd,
                GroupId = msg is { grouped_id: > 0 } ? msg.grouped_id : null,
                ThreadId = threadId,
                IsForwarded = msg?.fwd_from != null
            };
        }
    }

    // ─────────────────────── Messages ───────────────────────

    // Alias используется из ChannelExportService
    internal static Task<(List<MessageBase> sorted, Dictionary<long, User> users, Dictionary<long, ChatBase> chats)>
        LoadAllMessagesInternalAsync(Client client, InputPeer peer, ExportOptions opts, ExportProgressEmitter? emit, CancellationToken ct)
        => LoadAllMessagesAsync(client, peer, opts, emit, ct);

    private static async Task<(List<MessageBase> sorted, Dictionary<long, User> users, Dictionary<long, ChatBase> chats)>
        LoadAllMessagesAsync(Client client, InputPeer peer, ExportOptions opts, ExportProgressEmitter? emit, CancellationToken ct)
    {
        var users = new Dictionary<long, User>();
        var chats = new Dictionary<long, ChatBase>();
        var raw = new List<MessageBase>();

        var offsetId = 0;
        while (!ct.IsCancellationRequested)
        {
            var slice = await RateLimitedTelegram.ExecuteAsync(
                () => client.Messages_GetHistory(peer, offsetId, limit: opts.HistoryBatchSize),
                opts.DelayBetweenHistoryRequests, ct).ConfigureAwait(false);

            TL.Services.CollectUsersChats(slice, users, chats);
            if (slice.Messages.Length == 0) break;
            raw.AddRange(slice.Messages);

            emit?.MessagesLoadedCount = raw.Count;
            emit?.Pulse();

            var oldest = slice.Messages[^1].ID;
            if (oldest == offsetId) break;
            offsetId = oldest;
            if (slice.Messages.Length < opts.HistoryBatchSize) break;

            if (raw.Count % 500 < opts.HistoryBatchSize)
                emit?.Log($"  история… {raw.Count} сообщ.");
        }

        var byId = raw.GroupBy(m => m.ID).ToDictionary(g => g.Key, g => g.First());
        var sorted = byId.Values.Where(m => m is not MessageEmpty).OrderBy(m => m.ID).ToList();
        emit?.MessagesLoadedCount = sorted.Count;
        emit?.Pulse();
        return (sorted, users, chats);
    }

    // ─────────────────────── Participants ───────────────────────

    private static async Task<Dictionary<long, MemberData>> LoadParticipantsAsync(
        Client client, InputPeer peer,
        Dictionary<long, User> historyUsers, Dictionary<long, ChatBase> historyChats,
        ExportOptions opts, ExportProgressEmitter? emit, CancellationToken ct)
    {
        var allUsers = new Dictionary<long, User>(historyUsers);

        try
        {
            switch (peer)
            {
                case InputPeerChannel ipc:
                {
                    var ch = new InputChannel(ipc.channel_id, ipc.access_hash);
                    var offset = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        var batch = await RateLimitedTelegram.ExecuteAsync(
                            () => client.Channels_GetParticipants(ch, null, offset, 200, 0),
                            opts.DelayBetweenHistoryRequests, ct).ConfigureAwait(false);

                        foreach (var (id, u) in batch.users)
                            allUsers[id] = u;

                        if (batch.participants.Length == 0) break;
                        offset += batch.participants.Length;
                        emit?.SetParticipantFetch(offset, Math.Max(batch.count, 1));
                        if (offset >= batch.count) break;

                        emit?.Log($"  участники… {allUsers.Count}");
                    }
                    break;
                }
                case InputPeerChat ipc:
                {
                    var full = await client.Messages_GetFullChat(ipc.chat_id).ConfigureAwait(false);
                    foreach (var (id, u) in full.users)
                        allUsers[id] = u;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            emit?.Log($"  (участники через API: {ex.Message} — используем данные из истории)");
        }

        var result = new Dictionary<long, MemberData>();
        foreach (var (id, u) in allUsers)
        {
            result[id] = new MemberData
            {
                Name = UserName(u),
                Username = string.IsNullOrEmpty(u.username) ? null : u.username,
                Phone = string.IsNullOrEmpty(u.phone) ? null : u.phone,
                IsBot = u.IsBot,
                LastSeen = FormatUserStatus(u.status)
            };
        }

        emit?.BeginBiosPhase(allUsers.Count);
        emit?.Log($"Загрузка биографий ({allUsers.Count} уч.)…");
        var idx = 0;
        foreach (var (id, u) in allUsers)
        {
            ct.ThrowIfCancellationRequested();
            if (u.IsBot || u.access_hash == 0) { idx++; continue; }
            try
            {
                var full = await RateLimitedTelegram.ExecuteAsync(
                    () => client.Users_GetFullUser(new InputUser(u.id, u.access_hash)),
                    TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(full.full_user.about) && result.TryGetValue(id, out var md))
                    md.Bio = full.full_user.about;
            }
            catch { /* skip */ }
            idx++;
            if (idx % 5 == 0 || idx % 25 == 0)
                emit?.SetBioProgress(idx, allUsers.Count);
            if (idx % 25 == 0) emit?.Log($"  биографии… {idx}/{allUsers.Count}");
        }

        return result;
    }

    private static string FormatUserStatus(UserStatus? s) => s switch
    {
        UserStatusOnline => "В сети",
        UserStatusOffline o => $"Был(а) {o.was_online:dd.MM.yyyy HH:mm}",
        UserStatusRecently => "Был(а) недавно",
        UserStatusLastWeek => "На прошлой неделе",
        UserStatusLastMonth => "В прошлом месяце",
        _ => ""
    };

    // ─────────────────────── Build message data ───────────────────────

    private static MsgData BuildMsgData(MessageBase m, Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats, Dictionary<int, string> textById,
        bool yearMonthSubdir = false)
    {
        var msg = m as Message;
        var svc = m as MessageService;

        var fromId = ResolvePeerId(m.From) ?? ResolvePeerId(m.Peer) ?? 0;
        var author = ResolveAuthorName(m, users, chats);

        var isImported = msg?.fwd_from is { } fh && fh.flags.HasFlag(MessageFwdHeader.Flags.imported);

        string? fwd = null;
        if (isImported && !string.IsNullOrEmpty(msg!.fwd_from!.from_name))
        {
            author = msg.fwd_from.from_name;
            fromId = StableHash(author);
        }
        else if (msg?.fwd_from is { } f)
        {
            if (!string.IsNullOrEmpty(f.from_name)) fwd = f.from_name;
            else if (f.from_id is PeerUser pu && users.TryGetValue(pu.user_id, out var fu))
                fwd = UserName(fu);
            else if (f.from_id is PeerChannel pc && chats.TryGetValue(pc.channel_id, out var fc))
                fwd = GetChatTitle(fc);
        }

        int? replyTo = null;
        string? replySnippet = null;
        if (m.ReplyTo is MessageReplyHeader hdr)
        {
            replyTo = hdr.reply_to_msg_id;
            textById.TryGetValue(hdr.reply_to_msg_id, out replySnippet);
        }

        return new MsgData
        {
            Id = m.ID,
            Date = m.Date.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            FromId = fromId,
            Author = author,
            Text = msg?.message,
            ReplyTo = replyTo,
            ReplySnippet = replySnippet,
            MediaType = msg != null ? GuessMediaType(msg) : null,
            MediaPath = msg != null ? GuessMediaPath(msg, yearMonthSubdir) : null,
            Fwd = fwd,
            IsOut = msg is { flags: var fl } && fl.HasFlag(Message.Flags.out_),
            IsSvc = svc != null,
            SvcText = svc != null ? DescribeServiceAction(svc) : null,
            GrpId = msg is { grouped_id: > 0 } ? msg.grouped_id : null
        };
    }

    private static long StableHash(string s)
    {
        long h = 5381;
        foreach (var c in s) h = h * 33 + c;
        return h & 0x7FFFFFFF;
    }

    private static long? ResolvePeerId(Peer? p) => p switch
    {
        PeerUser pu => pu.user_id,
        PeerChat pc => pc.chat_id,
        PeerChannel pch => pch.channel_id,
        _ => null
    };

    private static string ResolveAuthorName(MessageBase m, Dictionary<long, User> users, Dictionary<long, ChatBase> chats)
    {
        if (m is Message msg && !string.IsNullOrEmpty(msg.post_author))
            return msg.post_author;

        Peer? from = m.From ?? m.Peer;
        if (from is null) return "?";

        return from switch
        {
            PeerUser pu when users.TryGetValue(pu.user_id, out var u) => UserName(u),
            PeerChat pc when chats.TryGetValue(pc.chat_id, out var c) => GetChatTitle(c),
            PeerChannel pch when chats.TryGetValue(pch.channel_id, out var ch) => GetChatTitle(ch),
            PeerUser pu => $"user_{pu.user_id}",
            PeerChat pc => $"chat_{pc.chat_id}",
            PeerChannel pch => $"channel_{pch.channel_id}",
            _ => "?"
        };
    }

    private static string GetChatTitle(ChatBase c) => c switch
    {
        Chat x => x.title ?? "?",
        Channel x => x.title ?? "?",
        ChatForbidden x => x.title ?? "?",
        ChannelForbidden x => x.title ?? "?",
        _ => "?"
    };

    private static string DescribeServiceAction(MessageService svc) => svc.action switch
    {
        MessageActionChatCreate a => $"создал(а) группу «{a.title}»",
        MessageActionChatEditTitle a => $"название → «{a.title}»",
        MessageActionChatEditPhoto => "обновил(а) фото группы",
        MessageActionChatDeletePhoto => "удалил(а) фото группы",
        MessageActionChatAddUser => "добавил(а) участника",
        MessageActionChatDeleteUser => "удалил(а) участника",
        MessageActionChatJoinedByLink => "вошёл по ссылке",
        MessageActionPinMessage => "закрепил(а) сообщение",
        MessageActionHistoryClear => "история очищена",
        MessageActionPhoneCall => "звонок",
        MessageActionChannelCreate a => $"создан канал «{a.title}»",
        MessageActionChatMigrateTo => "группа → супергруппа",
        _ => svc.action?.GetType().Name.Replace("MessageAction", "", StringComparison.Ordinal) ?? "событие"
    };

    private static string PlainPreview(MessageBase m) => m switch
    {
        Message { message: { Length: > 0 } t } => t.Length > 120 ? t[..120] + "…" : t,
        Message => "«медиа»",
        MessageService => "[служебное]",
        _ => "…"
    };

    private static string? GuessMediaType(Message msg)
    {
        if (msg.media is MessageMediaPhoto) return "photo";
        var doc = GuessPrimaryDocument(msg);
        if (doc is null) return null;
        return IsVideoDocument(doc) ? "video" : "doc";
    }

    internal static bool IsVideoDocument(Document doc)
    {
        if (doc.mime_type?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return doc.attributes?.Any(static a => a is DocumentAttributeVideo) == true;
    }

    private static string? GuessMediaPath(Message msg, bool yearMonthSubdir)
    {
        var sub = yearMonthSubdir ? msg.Date.ToLocalTime().ToString("yyyy-MM") + "/" : "";
        if (msg.media is MessageMediaPhoto { photo: Photo }) return $"media/{sub}photo_{msg.id}.jpg";
        var doc = GuessPrimaryDocument(msg);
        return doc is not null ? $"media/{sub}" + BuildDocumentFileName(msg.id, doc) : null;
    }

    internal static Document? GuessPrimaryDocumentInternal(Message msg) => GuessPrimaryDocument(msg);

    /// <summary>Основной вложенный документ для отображения в HTML (альбомы с платным медиа — первый доступный файл).</summary>
    private static Document? GuessPrimaryDocument(Message msg)
    {
        switch (msg.media)
        {
            case MessageMediaDocument mmd:
                return PickDocumentFromMediaDocument(mmd);
            case MessageMediaWebPage { webpage: WebPage wp }:
                return wp.document as Document;
            case MessageMediaPaidMedia paid:
                return PickFirstPaidDocument(paid);
            default:
                return null;
        }
    }

    private static Document? PickFirstPaidDocument(MessageMediaPaidMedia paid)
    {
        if (paid.extended_media is null) return null;
        foreach (var em in paid.extended_media)
        {
            if (em is not MessageExtendedMedia mex || mex.media is not MessageMediaDocument mmd) continue;
            var d = PickDocumentFromMediaDocument(mmd);
            if (d != null) return d;
        }
        return null;
    }

    internal static Document? PickDocumentInternal(MessageMediaDocument mmd) => PickDocumentFromMediaDocument(mmd);

    private static Document? PickDocumentFromMediaDocument(MessageMediaDocument mmd)
    {
        if (mmd.document is Document d) return d;
        if (mmd.alt_documents is { Length: > 0 } alts)
        {
            foreach (var alt in alts)
                if (alt is Document ad) return ad;
        }
        return null;
    }

    internal static string BuildDocumentFileNameInternal(int messageId, Document doc) => BuildDocumentFileName(messageId, doc);

    private static string BuildDocumentFileName(int messageId, Document doc)
    {
        var baseName = !string.IsNullOrEmpty(doc.Filename)
            ? SanitizeFileName(doc.Filename)
            : $"doc{GuessExt(doc.mime_type)}";
        return $"{messageId}_{doc.id}_{baseName}";
    }

    private static string GuessExt(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return ".bin";
        var slash = mime.IndexOf('/');
        return slash >= 0 && slash < mime.Length - 1 ? "." + mime[(slash + 1)..].Split(';')[0].Trim() : ".bin";
    }

    // ─────────────────────── HTML generation ───────────────────────

    private static async Task WriteHtmlAsync(
        string path, string title, int totalMessages,
        List<MsgData> msgs, Dictionary<long, MemberData> members,
        int chunkSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var enc = WebUtility.HtmlEncode(title);
        var msgsJson = JsonSerializer.Serialize(msgs, JsonOpts);
        var membersJson = JsonSerializer.Serialize(members, JsonOpts);

        await using var w = new StreamWriter(path, false, Encoding.UTF8, 65536);
        await w.WriteAsync(
$"""
<!DOCTYPE html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{enc} — архив</title>
<style>
{CssContent}
</style></head><body>
<aside id="sidebar">
<div class="sb-hd"><h2>Участники (<span id="member-count">0</span>)</h2>
<button class="btn-x" onclick="toggleSidebar()">&times;</button></div>
<input id="search-p" type="text" placeholder="Поиск…" oninput="filterP()">
<div id="p-list"></div>
</aside>
<div id="overlay" onclick="closeProf()"></div>
<div id="prof-card"><button class="btn-x" onclick="closeProf()" style="position:absolute;top:12px;right:16px">&times;</button><div id="prof-c"></div></div>
<main>
<header class="top-bar">
<div><h1>{enc}</h1><span class="top-sub">{totalMessages} сообщений</span></div>
<button class="btn-mem" onclick="toggleSidebar()">Участники</button>
</header>
<div id="messages"></div>
<div id="sentinel"></div>
<p id="status" class="st-text">Загрузка…</p>
</main>
<script>
const M=
""");

        await w.WriteAsync(msgsJson);
        await w.WriteAsync(";\nconst P=");
        await w.WriteAsync(membersJson);
        await w.WriteAsync($";\nconst CK={chunkSize};\n");
        await w.WriteAsync(JsContent);
        await w.WriteAsync("\n</script></body></html>");
    }

    // ─────────────────────── Media download ───────────────────────

    /// <summary>
    /// Скачивает все медиа чата с поддержкой параллельной загрузки, дедупликации,
    /// фильтрации по типу и группировки по альбомам.
    /// </summary>
    private static async Task DownloadAllMediaAsync(
        Client client, InputPeer peer, List<MessageBase> sorted, string outputRoot,
        ExportOptions opts, IncrementalState state, ExportProgressEmitter? emit, CancellationToken ct)
    {
        if (opts.Quality == MediaQuality.None)
        {
            emit?.Log("Скачивание медиа отключено (качество = «Без медиа»).");
            return;
        }

        var st = new MediaDownloadStats();
        var messagesWithMedia = sorted
            .OfType<Message>()
            .Where(m => m.media is not null)
            .ToList();
        var total = messagesWithMedia.Count;
        var processed = 0;
        var processedLock = new object();

        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string GetMediaDir(DateTime date, long? groupId = null)
        {
            var ym = date.ToLocalTime().ToString("yyyy-MM");
            var dir = opts.GroupAlbums && groupId is > 0
                ? Path.Combine(outputRoot, "media", ym, $"album_{groupId}")
                : Path.Combine(outputRoot, "media", ym);
            lock (createdDirs)
            {
                if (createdDirs.Add(dir)) Directory.CreateDirectory(dir);
            }
            return dir;
        }

        void ReportStats(string? hint = null)
        {
            int p; lock (processedLock) p = processed;
            emit?.SyncMedia(st, p, total, hint);
            var parts = new List<string>();
            if (st.Photos > 0) parts.Add($"фото: {st.Photos}");
            if (st.Videos > 0) parts.Add($"видео: {st.Videos}");
            if (st.Gifs > 0) parts.Add($"GIF: {st.Gifs}");
            if (st.Stickers > 0) parts.Add($"стикеры: {st.Stickers}");
            if (st.Voices > 0) parts.Add($"голос/кружки: {st.Voices}");
            if (st.Docs > 0) parts.Add($"документы: {st.Docs}");
            var downloaded = st.Photos + st.Videos + st.Gifs + st.Stickers + st.Voices + st.Docs;
            emit?.Log($"── Медиа [{p}/{total}] скачано {downloaded}, пропущено {st.Skipped} ──");
            if (parts.Count > 0)
                emit?.Log($"   {string.Join(" │ ", parts)}");
        }

        emit?.SyncMedia(st, 0, total, null);

        // Параллельная загрузка через SemaphoreSlim
        var sem = new SemaphoreSlim(Math.Max(1, opts.ParallelDownloads));
        var tasks = new List<Task>();

        foreach (var msg0 in messagesWithMedia)
        {
            ct.ThrowIfCancellationRequested();
            await sem.WaitAsync(ct).ConfigureAwait(false);

            var capturedMsg = msg0;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var msg = await EnsureMessageWithLoadedDocumentAsync(client, peer, capturedMsg, opts, emit, ct)
                        .ConfigureAwait(false);
                    var mediaDir = GetMediaDir(msg.Date, msg.grouped_id > 0 ? msg.grouped_id : null);

                    await DownloadSingleMessageMediaAsync(client, peer, msg, mediaDir, outputRoot, opts, state, st, emit, ct)
                        .ConfigureAwait(false);

                    int p; lock (processedLock) p = ++processed;
                    if (p % 8 == 0) ReportStats();
                }
                catch (OperationCanceledException) { /* отменено */ }
                catch (Exception ex)
                {
                    emit?.Log($"  медиа #{capturedMsg.id}: {ex.Message}");
                    lock (processedLock) processed++;
                }
                finally { sem.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Сохраняем обновлённые данные дедупликации
        state.TotalMediaBytes = st.TotalBytes;
        ReportStats();
        emit?.Log("── Скачивание медиа завершено ──");
    }

    private static async Task DownloadSingleMessageMediaAsync(
        Client client, InputPeer? peer, Message msg, string mediaDir, string outputRoot,
        ExportOptions opts, IncrementalState state, MediaDownloadStats st,
        ExportProgressEmitter? emit, CancellationToken ct)
    {
        switch (msg.media)
        {
            case MessageMediaPhoto { photo: Photo photo }:
            {
                if (!opts.DownloadPhotos) return;
                var fn = $"photo_{msg.id}.jpg";
                var fp = Path.Combine(mediaDir, fn);
                if (File.Exists(fp) && new FileInfo(fp).Length > 0) { lock (st) st.Skipped++; return; }

                emit?.SetMediaFileHint(fn);
                PhotoSizeBase? targetSize = opts.Quality switch
                {
                    MediaQuality.Thumbs => PickSmallestSize(photo),
                    MediaQuality.Compressed => PickMediumSize(photo),
                    _ => null
                };
                await RateLimitedTelegram.ExecuteAsync(async () =>
                {
                    await using var fs = File.Create(fp);
                    await client.DownloadFileAsync(photo, fs, targetSize).ConfigureAwait(false);
                }, opts.DelayAfterEachMediaFile, ct).ConfigureAwait(false);

                var len = new FileInfo(fp).Length;
                if (len == 0)
                {
                    emit?.Log($"  фото #{msg.id}: пустой файл, повтор через 3с…");
                    try { File.Delete(fp); } catch { /* */ }
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    await RateLimitedTelegram.ExecuteAsync(async () =>
                    {
                        await using var fs = File.Create(fp);
                        await client.DownloadFileAsync(photo, fs).ConfigureAwait(false);
                    }, opts.DelayAfterEachMediaFile, ct).ConfigureAwait(false);
                    len = new FileInfo(fp).Length;
                }

                // Дедупликация
                if (opts.DeduplicateMedia && len > 0)
                {
                    var relPath = Path.GetRelativePath(outputRoot, fp).Replace('\\', '/');
                    var dup = IncrementalStateService.CheckAndRegisterDedup(state, fp, relPath);
                    if (dup != null) emit?.Log($"  фото #{msg.id}: дубликат {dup}");
                }

                lock (st) { st.Photos++; st.TotalBytes += len; }

                // Копия в _forwarded если пересланное
                if (opts.TrackForwarded && msg.fwd_from != null && len > 0)
                {
                    var fwdDir = Path.Combine(outputRoot, "_forwarded");
                    Directory.CreateDirectory(fwdDir);
                    var fwdFp = Path.Combine(fwdDir, fn);
                    if (!File.Exists(fwdFp))
                        try { File.Copy(fp, fwdFp); } catch { /* */ }
                }
                break;
            }

            case MessageMediaDocument mmd:
            {
                if (opts.Quality == MediaQuality.Thumbs) return;
                var doc = PickDocumentFromMediaDocument(mmd);
                if (doc is null)
                {
                    emit?.Log($"  сообщ. #{msg.id}: документ недоступен — пропуск");
                    return;
                }

                // Фильтр по типу документа
                var isVid = IsVideoDocument(doc);
                var isSticker = doc.attributes?.Any(a => a is DocumentAttributeSticker) == true;
                var isVoice = doc.attributes?.Any(a => a is DocumentAttributeAudio { flags: var af }
                    && af.HasFlag(DocumentAttributeAudio.Flags.voice)) == true;

                if (isVid && !opts.DownloadVideos) return;
                if (isSticker && !opts.DownloadStickers) return;
                if (isVoice && !opts.DownloadVoice) return;
                if (!isVid && !isSticker && !isVoice && !opts.DownloadDocuments) return;

                emit?.SetMediaFileHint(BuildDocumentFileName(msg.id, doc));
                await DownloadDocumentWithRetryAsync(client, peer, msg.id, doc, mediaDir, opts, emit, ct, st).ConfigureAwait(false);

                // Дедупликация
                if (opts.DeduplicateMedia)
                {
                    var fn2 = BuildDocumentFileName(msg.id, doc);
                    var fp2 = Path.Combine(mediaDir, fn2);
                    if (File.Exists(fp2) && new FileInfo(fp2).Length > 0)
                    {
                        var relPath = Path.GetRelativePath(outputRoot, fp2).Replace('\\', '/');
                        var dup = IncrementalStateService.CheckAndRegisterDedup(state, fp2, relPath);
                        if (dup != null) emit?.Log($"  файл #{msg.id}: дубликат {dup}");
                    }
                }

                // Копия в _forwarded
                if (opts.TrackForwarded && msg.fwd_from != null)
                {
                    var fn2 = BuildDocumentFileName(msg.id, doc);
                    var fp2 = Path.Combine(mediaDir, fn2);
                    if (File.Exists(fp2))
                    {
                        var fwdDir = Path.Combine(outputRoot, "_forwarded");
                        Directory.CreateDirectory(fwdDir);
                        var fwdFp = Path.Combine(fwdDir, fn2);
                        if (!File.Exists(fwdFp))
                            try { File.Copy(fp2, fwdFp); } catch { /* */ }
                    }
                }
                break;
            }

            case MessageMediaWebPage { webpage: WebPage wp }:
            {
                if (wp.document is not Document wdoc) return;
                if (opts.Quality == MediaQuality.Thumbs) return;
                await DownloadDocumentWithRetryAsync(client, peer, msg.id, wdoc, mediaDir, opts, emit, ct, st).ConfigureAwait(false);
                break;
            }

            case MessageMediaPaidMedia paid:
            {
                if (paid.extended_media is null) return;
                foreach (var em in paid.extended_media)
                {
                    ct.ThrowIfCancellationRequested();
                    if (em is not MessageExtendedMedia mex) continue;
                    await DownloadSingleMessageMediaAsync(
                        client, peer, new Message { id = msg.id, date = msg.date, media = mex.media, fwd_from = msg.fwd_from },
                        mediaDir, outputRoot, opts, state, st, emit, ct).ConfigureAwait(false);
                }
                break;
            }
        }
    }

    internal static Task<Message> EnsureMessageInternalAsync(
        Client client, InputPeer peer, Message msg, ExportOptions opts, ExportProgressEmitter? emit,
        CancellationToken ct)
        => EnsureMessageWithLoadedDocumentAsync(client, peer, msg, opts, emit, ct);

    /// <summary>
    /// Иногда в истории приходит «урезанный» документ; повторный запрос по ID подгружает полный <see cref="Document"/>.
    /// </summary>
    private static async Task<Message> EnsureMessageWithLoadedDocumentAsync(
        Client client, InputPeer peer, Message msg, ExportOptions opts, ExportProgressEmitter? emit,
        CancellationToken ct)
    {
        if (msg.media is not MessageMediaDocument mmd || mmd.document is not DocumentEmpty)
            return msg;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(280 * (attempt + 1)), ct).ConfigureAwait(false);
            emit?.Log($"  сообщ. #{msg.id}: дозагрузка метаданных файла (попытка {attempt + 1}/3)…");
            var refreshed = await TryGetMessageByIdAsync(client, peer, msg.id, opts, ct).ConfigureAwait(false);
            if (refreshed?.media is MessageMediaDocument m2 && m2.document is Document)
                return refreshed;
            if (refreshed is not null)
                return refreshed;
        }

        return msg;
    }

    private static async Task<Message?> TryGetMessageByIdAsync(
        Client client, InputPeer peer, int messageId, ExportOptions opts, CancellationToken ct)
    {
        var batch = await RateLimitedTelegram.ExecuteAsync(
            () => client.GetMessages(peer, new InputMessage[] { new InputMessageID { id = messageId } }),
            opts.DelayBetweenHistoryRequests, ct).ConfigureAwait(false);

        foreach (var mb in batch.Messages)
            if (mb is Message mm && mm.id == messageId)
                return mm;
        return null;
    }

    internal class MediaDownloadStats
    {
        public int Photos, Videos, Gifs, Stickers, Voices, Docs, Skipped;
        public long TotalBytes;
    }

    internal static Task DownloadDocumentInternalAsync(
        Client client, InputPeer? peer, int messageId, Document doc, string mediaDir, ExportOptions opts,
        ExportProgressEmitter? emit, CancellationToken ct, MediaDownloadStats st)
        => DownloadDocumentWithRetryAsync(client, peer, messageId, doc, mediaDir, opts, emit, ct, st);

    private static async Task DownloadDocumentWithRetryAsync(
        Client client, InputPeer? peer, int messageId, Document doc, string mediaDir, ExportOptions opts,
        ExportProgressEmitter? emit, CancellationToken ct, MediaDownloadStats st)
    {
        var mime = doc.mime_type ?? "";
        var isVideo = IsVideoDocument(doc);
        var isGif = mime.Equals("image/gif", StringComparison.OrdinalIgnoreCase)
                    || doc.attributes?.Any(a => a is DocumentAttributeAnimated) == true;
        var isSticker = doc.attributes?.Any(a => a is DocumentAttributeSticker) == true;
        var isVoice = doc.attributes?.Any(a => a is DocumentAttributeAudio { flags: var af }
                          && af.HasFlag(DocumentAttributeAudio.Flags.voice)) == true
                      || mime.StartsWith("audio/ogg", StringComparison.OrdinalIgnoreCase);
        var isRound = doc.attributes?.Any(a => a is DocumentAttributeVideo { flags: var vf }
                          && vf.HasFlag(DocumentAttributeVideo.Flags.round_message)) == true;

        var fn = BuildDocumentFileName(messageId, doc);
        var fp = Path.Combine(mediaDir, fn);

        if (File.Exists(fp) && doc.size > 0 && new FileInfo(fp).Length == doc.size)
        {
            st.Skipped++;
            return;
        }
        if (File.Exists(fp))
        {
            try { File.Delete(fp); }
            catch { /* перезапишем ниже */ }
        }

        var maxAttempts = TelegramDownloadHelper.GetDownloadMaxAttempts(doc, isVideo);
        var stallMinutes = TelegramDownloadHelper.GetStallTimeoutMinutes(doc, isVideo);

        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // CTS с таймаутом зависания (сбрасывается при каждой порции данных)
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lastActivity = DateTime.UtcNow;

            // Фоновый watchdog: отменяет загрузку если нет прогресса
            var watchdog = Task.Run(async () =>
            {
                while (!stallCts.IsCancellationRequested)
                {
                    await Task.Delay(15_000, stallCts.Token).ConfigureAwait(false);
                    if ((DateTime.UtcNow - lastActivity).TotalMinutes >= stallMinutes)
                    {
                        stallCts.Cancel();
                        return;
                    }
                }
            }).ContinueWith(_ => { });

            try
            {
                await Task.Delay(opts.DelayAfterEachMediaFile, stallCts.Token).ConfigureAwait(false);

                var resumeFrom = 0L;
                if (File.Exists(fp))
                {
                    resumeFrom = new FileInfo(fp).Length;
                    if (doc.size > 0 && resumeFrom >= doc.size)
                    {
                        st.Skipped++;
                        return;
                    }
                    if (resumeFrom > doc.size && doc.size > 0)
                    {
                        try { File.Delete(fp); } catch { /* */ }
                        resumeFrom = 0;
                    }
                }

                if (resumeFrom > 0)
                    emit?.Log($"  ↻ Докачка {fn}: с {FormatFileSize(resumeFrom)} / {FormatFileSize(doc.size)}…");

                await using var fs = new FileStream(fp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (resumeFrom > 0)
                    fs.Seek(resumeFrom, SeekOrigin.Begin);
                else
                    fs.SetLength(0);

                await client.DownloadFileAsync(doc, fs, progress: (bytes, total) =>
                {
                    // Не бросать исключение из callback — иначе при «Стоп» оно не ловится снаружи
                    if (ct.IsCancellationRequested)
                        return;
                    lastActivity = DateTime.UtcNow; // Сбрасываем watchdog при каждом чанке
                    // Логируем прогресс видео каждые ~25%
                    if (isVideo && total > 0 && emit != null)
                    {
                        var pct = (int)(bytes * 100 / total);
                        if (pct is >= 25 and < 50 && bytes - total / 4 < 524_288)
                            emit.Log($"  ↓ {fn}: 25% ({FormatFileSize(bytes)}/{FormatFileSize(total)})");
                        else if (pct is >= 50 and < 75 && bytes - total / 2 < 524_288)
                            emit.Log($"  ↓ {fn}: 50% ({FormatFileSize(bytes)}/{FormatFileSize(total)})");
                        else if (pct is >= 75 and < 100 && bytes - total * 3 / 4 < 524_288)
                            emit.Log($"  ↓ {fn}: 75% ({FormatFileSize(bytes)}/{FormatFileSize(total)})");
                    }
                }).ConfigureAwait(false);

                stallCts.Cancel(); // Останавливаем watchdog
                await watchdog.ConfigureAwait(false);

                var len = new FileInfo(fp).Length;

                // Файл скачался нулевым — Telegram ещё не синхронизировал, или истёк file_reference
                if (len == 0 && doc.size > 0)
                {
                    try { File.Delete(fp); } catch { /* */ }
                    if (peer != null)
                    {
                        var refreshed = await TryGetMessageByIdAsync(client, peer, messageId, opts, ct)
                            .ConfigureAwait(false);
                        if (refreshed?.media is MessageMediaDocument mmd2 && mmd2.document is Document freshDoc)
                        {
                            doc = freshDoc;
                            fn = BuildDocumentFileName(messageId, doc);
                            fp = Path.Combine(mediaDir, fn);
                            emit?.Log($"  #{messageId}: обновлена ссылка, повтор {attempt}/{maxAttempts}…");
                        }
                        else
                        {
                            var waitSec = isVideo ? attempt * 4 : attempt * 2;
                            emit?.Log($"  {fn}: пустой файл, жду {waitSec}с (попытка {attempt}/{maxAttempts})…");
                            await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var waitSec = isVideo ? attempt * 4 : attempt * 2;
                        emit?.Log($"  {fn}: пустой файл, жду {waitSec}с (попытка {attempt}/{maxAttempts})…");
                        await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
                    }
                    continue;
                }

                if (doc.size > 0 && len != doc.size && attempt < maxAttempts)
                {
                    emit?.Log($"  {fn}: размер {len} ≠ {doc.size}, повтор {attempt}/{maxAttempts}…");
                    try { File.Delete(fp); } catch { /* */ }
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                st.TotalBytes += len;
                if (isGif) st.Gifs++;
                else if (isSticker) st.Stickers++;
                else if (isVoice || isRound) st.Voices++;
                else if (isVideo) st.Videos++;
                else st.Docs++;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (File.Exists(fp)) try { File.Delete(fp); } catch { /* */ }
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Зависание обнаружено watchdog'ом — обновляем ссылку и повторяем
                var stallPartial = File.Exists(fp) ? new FileInfo(fp).Length : 0L;
                emit?.Log(stallPartial > 0
                    ? $"  {fn}: нет данных {stallMinutes:0.#} мин — докачка с {FormatFileSize(stallPartial)} ({attempt}/{maxAttempts})…"
                    : $"  {fn}: загрузка зависла ({stallMinutes:0.#} мин), повтор {attempt}/{maxAttempts}…");
                (doc, fn, fp) = await RefreshDocumentReferenceAsync(client, peer, messageId, opts, ct, doc, fn, fp, mediaDir, emit)
                    .ConfigureAwait(false);
                last = new TimeoutException($"Загрузка зависла: {fn}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (RpcException ex) when (
                ex.Message.Contains("FILE_REFERENCE_EXPIRED") ||
                ex.Message.Contains("FILE_REFERENCE_INVALID"))
            {
                emit?.Log($"  {fn}: {ex.Message} — обновляю ссылку… (попытка {attempt}/{maxAttempts})");
                if (File.Exists(fp)) try { File.Delete(fp); } catch { /* новая ссылка — с начала */ }
                (doc, fn, fp) = await RefreshDocumentReferenceAsync(client, peer, messageId, opts, ct, doc, fn, fp, mediaDir, emit)
                    .ConfigureAwait(false);
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(800 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (TelegramDownloadHelper.IsTransientDownloadError(ex))
            {
                last = ex;
                var partial = File.Exists(fp) ? new FileInfo(fp).Length : 0L;
                emit?.Log(partial > 0
                    ? $"  {fn}: обрыв соединения — сохранено {FormatFileSize(partial)}, повтор {attempt}/{maxAttempts}…"
                    : $"  {fn}: обрыв соединения — повтор {attempt}/{maxAttempts}…");
                (doc, fn, fp) = await RefreshDocumentReferenceAsync(client, peer, messageId, opts, ct, doc, fn, fp, mediaDir, emit)
                    .ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromSeconds(TelegramDownloadHelper.RetryDelaySeconds(attempt, partial)), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
                emit?.Log($"  {fn}: {ex.Message} (попытка {attempt}/{maxAttempts})");
                if (File.Exists(fp)) try { File.Delete(fp); } catch { /* */ }
                await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), ct).ConfigureAwait(false);
            }
            finally
            {
                stallCts.Cancel();
                await watchdog.ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
        if (last != null)
        {
            var partial = File.Exists(fp) ? new FileInfo(fp).Length : 0L;
            emit?.Log(partial > 0
                ? $"  ✗ {fn}: не скачан ({last.Message}). Частично: {FormatFileSize(partial)} — повторите экспорт для докачки."
                : $"  ✗ {fn}: не скачан ({last.Message})");
            st.Skipped++;
        }
    }

    private static async Task<(Document Doc, string Fn, string Fp)> RefreshDocumentReferenceAsync(
        Client client, InputPeer? peer, int messageId, ExportOptions opts, CancellationToken ct,
        Document doc, string fn, string fp, string mediaDir, ExportProgressEmitter? emit)
    {
        if (peer is null) return (doc, fn, fp);
        var refreshed = await TryGetMessageByIdAsync(client, peer, messageId, opts, ct).ConfigureAwait(false);
        if (refreshed?.media is not MessageMediaDocument mmd2 || mmd2.document is not Document freshDoc)
            return (doc, fn, fp);
        doc = freshDoc;
        var newFn = BuildDocumentFileName(messageId, doc);
        if (!string.Equals(newFn, fn, StringComparison.OrdinalIgnoreCase))
        {
            fn = newFn;
            fp = Path.Combine(mediaDir, fn);
        }
        emit?.Log($"  #{messageId}: обновлена ссылка на файл");
        return (doc, fn, fp);
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} ГБ",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} МБ",
        >= 1_024         => $"{bytes / 1_024.0:F0} КБ",
        _                => $"{bytes} Б"
    };

    private static PhotoSizeBase? PickSmallestSize(Photo photo)
    {
        return photo.sizes?.OrderBy(SizePixels).FirstOrDefault();
    }

    private static PhotoSizeBase? PickMediumSize(Photo photo)
    {
        if (photo.sizes is null || photo.sizes.Length == 0) return null;
        var ordered = photo.sizes.OrderBy(SizePixels).ToArray();
        return ordered.Length > 1 ? ordered[ordered.Length / 2] : ordered[0];
    }

    private static long SizePixels(PhotoSizeBase s) => s switch
    {
        PhotoSize ps => (long)ps.w * ps.h,
        PhotoSizeProgressive ps => (long)ps.w * ps.h,
        PhotoCachedSize ps => (long)ps.w * ps.h,
        PhotoStrippedSize => 0,
        _ => long.MaxValue
    };

    private static string SanitizeFileName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "file" : n.Trim();
    }

    // ─────────────────────── Data types ───────────────────────

    private sealed class MsgData
    {
        public int Id { get; init; }
        public string Date { get; init; } = "";
        public long FromId { get; init; }
        public string Author { get; init; } = "";
        public string? Text { get; init; }
        public int? ReplyTo { get; init; }
        public string? ReplySnippet { get; init; }
        public string? ReplyAuthor { get; set; }
        public string? MediaType { get; init; }
        public string? MediaPath { get; init; }
        public string? Fwd { get; init; }
        public bool IsOut { get; init; }
        public bool IsSvc { get; init; }
        public string? SvcText { get; init; }
        public long? GrpId { get; init; }
    }

    private sealed class MemberData
    {
        public string Name { get; init; } = "";
        public string? Username { get; init; }
        public string? Bio { get; set; }
        public string? Phone { get; init; }
        public string? LastSeen { get; init; }
        public bool IsBot { get; init; }
    }

    // ─────────────────────── CSS ───────────────────────

    private const string CssContent = """
:root{--bg:#0e1621;--pn:#17212b;--ac:#5288c1;--tx:#e4edf5;--mt:#7d8b99;--bi:#182533;--bo:#2b5278;--bd:#243b53;--r:12px}
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:"Segoe UI Variable","Segoe UI",system-ui,sans-serif;background:var(--bg);color:var(--tx);min-height:100vh}
main{max-width:860px;margin:0 auto;padding:0 16px 48px}
.top-bar{position:sticky;top:0;z-index:10;display:flex;justify-content:space-between;align-items:center;
  background:var(--pn);border-bottom:1px solid var(--bd);padding:14px 20px;border-radius:0 0 var(--r) var(--r);backdrop-filter:blur(10px)}
.top-bar h1{font-size:1.25rem;font-weight:600}
.top-sub{color:var(--mt);font-size:.85rem}
.btn-mem{background:rgba(82,136,193,.1);color:var(--ac);border:1px solid var(--bd);border-radius:8px;padding:8px 16px;cursor:pointer;font-weight:600;font-size:.9rem}
.btn-mem:hover{background:rgba(82,136,193,.2)}

.date-sep{text-align:center;color:var(--mt);font-size:.8rem;margin:24px 0 12px;position:relative}
.date-sep::before{content:'';position:absolute;left:0;right:0;top:50%;border-top:1px solid var(--bd)}
.date-sep span{position:relative;background:var(--bg);padding:0 12px}

.msg{display:flex;gap:10px;margin:6px 0;padding:10px 14px;background:var(--bi);border-radius:var(--r);
  border:1px solid var(--bd);scroll-margin-top:72px;transition:background .3s}
.msg.out{margin-left:48px;background:var(--bo);border-color:#355f8a}
.msg.hl{background:rgba(82,136,193,.3)!important}
.msg.svc{justify-content:center;background:transparent;border:none;color:var(--mt);font-style:italic;font-size:.85rem}

.avatar{flex-shrink:0;width:36px;height:36px;border-radius:50%;display:flex;align-items:center;justify-content:center;
  color:#fff;font-weight:700;font-size:.75rem;cursor:pointer;user-select:none}
.msg-content{flex:1;min-width:0}
.msg-hd{display:flex;align-items:baseline;gap:8px;margin-bottom:4px;font-size:.85rem}
.who{font-weight:600;cursor:pointer}.who:hover{text-decoration:underline}
time{color:var(--mt);font-variant-numeric:tabular-nums;margin-left:auto;white-space:nowrap}

.reply{margin-bottom:6px;padding:6px 10px;border-left:3px solid var(--ac);background:rgba(0,0,0,.15);
  border-radius:0 8px 8px 0;cursor:pointer;overflow:hidden;transition:background .15s}
.reply:hover{background:rgba(0,0,0,.3)}
.reply-author{font-weight:600;font-size:.82rem;margin-bottom:2px}
.reply-text{color:var(--mt);font-size:.82rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:500px;line-height:1.3}
.fwd{font-size:.8rem;color:var(--mt);margin-bottom:4px}
.text{white-space:pre-wrap;line-height:1.45;word-break:break-word}

.msg-photo{max-width:100%;border-radius:10px;display:block;margin-top:6px;box-shadow:0 4px 16px rgba(0,0,0,.3)}
.msg-video{max-width:100%;border-radius:10px;display:block;margin-top:6px}
.msg-doc{display:inline-block;color:var(--ac);text-decoration:none;margin-top:6px}
.msg-doc:hover{text-decoration:underline}
.media-ph{color:var(--mt);font-size:.85rem;padding:8px}

#sidebar{position:fixed;top:0;right:-340px;width:340px;height:100vh;background:var(--pn);
  border-left:1px solid var(--bd);z-index:100;transition:right .3s;display:flex;flex-direction:column}
#sidebar.open{right:0}
.sb-hd{display:flex;justify-content:space-between;align-items:center;padding:16px 20px;border-bottom:1px solid var(--bd)}
.sb-hd h2{font-size:1.1rem;font-weight:600}
.btn-x{background:none;border:none;color:var(--mt);font-size:1.3rem;cursor:pointer}
.btn-x:hover{color:var(--tx)}
#search-p{margin:10px 16px;padding:8px 12px;background:var(--bg);border:1px solid var(--bd);border-radius:8px;color:var(--tx);font-size:.9rem;outline:none}
#search-p:focus{border-color:var(--ac)}
#p-list{flex:1;overflow-y:auto;padding:4px 0}
.p-item{display:flex;align-items:center;gap:10px;padding:8px 20px;cursor:pointer;transition:background .15s}
.p-item:hover{background:rgba(82,136,193,.1)}
.p-av{flex-shrink:0;width:40px;height:40px;border-radius:50%;display:flex;align-items:center;justify-content:center;color:#fff;font-weight:700;font-size:.8rem}
.p-info{min-width:0}
.p-name{font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;display:block}
.p-count{font-size:.8rem;color:var(--mt)}

#overlay{display:none;position:fixed;inset:0;background:rgba(0,0,0,.5);z-index:200}
#overlay.open{display:block}
#prof-card{display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);
  background:var(--pn);border:1px solid var(--bd);border-radius:16px;padding:28px;width:360px;z-index:300;text-align:center}
#prof-card.open{display:block}
.prof-av{width:72px;height:72px;border-radius:50%;display:flex;align-items:center;justify-content:center;
  color:#fff;font-size:1.5rem;font-weight:700;margin:0 auto 12px}
#prof-card h3{font-size:1.15rem;margin-bottom:4px}
.prof-row{color:var(--mt);font-size:.9rem;margin-top:4px}
.prof-bio{margin-top:10px;padding:10px;background:var(--bg);border-radius:8px;font-size:.9rem;line-height:1.4;text-align:left;white-space:pre-wrap}
.prof-badge{margin-top:8px;color:var(--ac);font-weight:600}

.st-text{text-align:center;color:var(--mt);font-size:.85rem;padding:16px}
""";

    // ─────────────────────── JS ───────────────────────

    private const string JsContent = """
const CL=['#e17076','#7bc862','#e5ca77','#65aadd','#a695e7','#ee7aae','#6ec9cb','#faa774'];
function esc(s){return s?s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'):''}
function nl(s){return esc(s).replace(/\n/g,'<br>')}
function clr(id){return CL[((id%CL.length)+CL.length)%CL.length]}
function ini(n){const p=n.trim().split(/\s+/);return(p[0]?.[0]||'?')+(p[1]?.[0]||'')}

const MC={},FI={};M.forEach(m=>{MC[m.fromId]=(MC[m.fromId]||0)+1;FI[m.id]=m.fromId});
let cur=0;const $m=document.getElementById('messages'),$se=document.getElementById('sentinel'),$st=document.getElementById('status');

function load(){
  if(cur>=M.length){$st.textContent=`Все ${M.length} сообщений загружены`;return}
  const e=Math.min(cur+CK,M.length),f=document.createDocumentFragment();
  let ld=cur>0?M[cur-1].date.split(' ')[0]:'';
  for(let i=cur;i<e;i++){
    const m=M[i],d=m.date.split(' ')[0];
    if(d!==ld){const s=document.createElement('div');s.className='date-sep';s.innerHTML='<span>'+esc(d)+'</span>';f.appendChild(s);ld=d}
    f.appendChild(rM(m))
  }
  $m.appendChild(f);cur=e;
  $st.textContent=cur>=M.length?`Все ${M.length} сообщений`:`${cur} из ${M.length}…`
}

function rM(m){
  const el=document.createElement('article');el.id='msg-'+m.id;
  if(m.isSvc){el.className='msg svc';el.textContent=m.svcText||'событие';return el}
  el.className='msg'+(m.isOut?' out':'');
  const p=P[m.fromId],nm=p?p.name:m.author,c=clr(m.fromId);
  let h='<div class="avatar" style="background:'+c+'" onclick="prof('+m.fromId+')">'+ini(nm)+'</div><div class="msg-content">';
  h+='<div class="msg-hd"><span class="who" style="color:'+c+'" onclick="prof('+m.fromId+')">'+esc(nm)+'</span><time>'+esc(m.date)+'</time></div>';
  if(m.replyTo){var rc=FI[m.replyTo]!=null?clr(FI[m.replyTo]):'var(--ac)';h+='<div class="reply" onclick="goMsg('+m.replyTo+')" style="border-left-color:'+rc+'">';if(m.replyAuthor)h+='<div class="reply-author" style="color:'+rc+'">'+esc(m.replyAuthor)+'</div>';if(m.replySnippet)h+='<div class="reply-text">'+esc(m.replySnippet)+'</div>';h+='</div>'}
  if(m.fwd)h+='<div class="fwd">\u21aa '+esc(m.fwd)+'</div>';
  h+='<div class="msg-body">';
  if(m.text)h+='<div class="text">'+nl(m.text)+'</div>';
  if(m.mediaType==='photo')h+='<img class="msg-photo" src="'+esc(m.mediaPath)+'" loading="lazy" onerror="this.outerHTML=\'<div class=media-ph>\ud83d\udcf7 \u0424\u043e\u0442\u043e</div>\'"/>';
  else if(m.mediaType==='video')h+='<video class="msg-video" controls preload="none" src="'+esc(m.mediaPath)+'"></video>';
  else if(m.mediaType==='doc')h+='<a class="msg-doc" href="'+esc(m.mediaPath)+'" download>\ud83d\udcce '+esc((m.mediaPath||'').split('/').pop()||'\u0424\u0430\u0439\u043b')+'</a>';
  h+='</div></div>';el.innerHTML=h;return el
}

function goMsg(id){
  let el=document.getElementById('msg-'+id);
  if(!el){let n=0;while(!el&&cur<M.length&&n<200){load();el=document.getElementById('msg-'+id);n++}}
  if(el){el.scrollIntoView({behavior:'smooth',block:'center'});el.classList.add('hl');setTimeout(()=>el.classList.remove('hl'),2500)}
}

new IntersectionObserver(e=>{if(e[0].isIntersecting)load()},{rootMargin:'800px'}).observe($se);
load();

function toggleSidebar(){document.getElementById('sidebar').classList.toggle('open')}
function prof(id){
  const p=P[id];if(!p)return;const c=MC[id]||0;
  document.getElementById('prof-c').innerHTML=
    '<div class="prof-av" style="background:'+clr(id)+'">'+ini(p.name)+'</div>'+
    '<h3>'+esc(p.name)+'</h3>'+
    (p.username?'<div class="prof-row">@'+esc(p.username)+'</div>':'')+
    (p.bio?'<div class="prof-bio">'+nl(p.bio)+'</div>':'')+
    '<div class="prof-row">ID: '+id+'</div>'+
    (p.lastSeen?'<div class="prof-row">'+esc(p.lastSeen)+'</div>':'')+
    '<div class="prof-row">\u0421\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u0439: '+c+'</div>'+
    (p.isBot?'<div class="prof-badge">\ud83e\udd16 \u0411\u043e\u0442</div>':'')+
    (p.phone?'<div class="prof-row">\ud83d\udcde '+esc(p.phone)+'</div>':'');
  document.getElementById('prof-card').classList.add('open');
  document.getElementById('overlay').classList.add('open')
}
function closeProf(){document.getElementById('prof-card').classList.remove('open');document.getElementById('overlay').classList.remove('open')}
function filterP(){const q=document.getElementById('search-p').value.toLowerCase();document.querySelectorAll('.p-item').forEach(e=>{e.style.display=e.dataset.n.toLowerCase().includes(q)?'':'none'})}

(function(){
  const l=document.getElementById('p-list');
  const s=Object.entries(P).sort((a,b)=>(MC[b[0]]||0)-(MC[a[0]]||0));
  for(const[id,p]of s){
    const e=document.createElement('div');e.className='p-item';e.dataset.n=p.name+(p.username||'');
    e.onclick=()=>prof(+id);
    e.innerHTML='<span class="p-av" style="background:'+clr(+id)+'">'+ini(p.name)+'</span>'+
      '<span class="p-info"><span class="p-name">'+esc(p.name)+'</span><span class="p-count">'+(MC[id]||0)+' \u0441\u043e\u043e\u0431\u0449.</span></span>';
    l.appendChild(e)
  }
  document.getElementById('member-count').textContent=s.length
})();
""";

    internal sealed class ExportProgressEmitter
    {
        private const double WeightText = 0.40;
        private const double WeightMedia = 0.45;
        private const double WeightAux = 0.15;

        private readonly IProgress<ExportProgressReport> _target;

        public ExportProgressEmitter(IProgress<ExportProgressReport> target) => _target = target;

        public ExportWorkPhase Phase { get; private set; } = ExportWorkPhase.History;
        public int MessagesLoadedCount { get; set; }
        public int MediaProcessed { get; set; }
        public int MediaTotal { get; set; }
        public int Photos { get; set; }
        public int Videos { get; set; }
        public int Gifs { get; set; }
        public int Stickers { get; set; }
        public int Voices { get; set; }
        public int Docs { get; set; }
        public int Skipped { get; set; }
        public long BytesDownloaded { get; set; }
        public string? CurrentFileHint { get; set; }
        public int ParticipantFetchOffset { get; private set; }
        public int ParticipantFetchTotal { get; private set; }
        public int BioStep { get; private set; }
        public int BioTotal { get; private set; }
        public bool BioPhase { get; private set; }

        private double _textSegment;
        private double _mediaSegment;
        private double _auxSegment;
        private bool _mediaEnabled = true;
        private bool _auxEnabled = true;
        private int _auxStep;
        private int _auxTotal;

        public void ConfigureSegments(bool mediaEnabled, bool auxEnabled)
        {
            _mediaEnabled = mediaEnabled;
            _auxEnabled = auxEnabled;
            if (!_mediaEnabled) _mediaSegment = 1;
            if (!_auxEnabled) _auxSegment = 1;
        }

        public void SetPhase(ExportWorkPhase phase)
        {
            Phase = phase;
            switch (phase)
            {
                case ExportWorkPhase.History:
                    _textSegment = Math.Max(_textSegment, 0.04);
                    break;
                case ExportWorkPhase.Participants:
                    _textSegment = Math.Max(_textSegment, 0.55);
                    break;
                case ExportWorkPhase.Html:
                    _textSegment = Math.Max(_textSegment, 0.88);
                    break;
                case ExportWorkPhase.Media:
                    _textSegment = 1;
                    break;
                case ExportWorkPhase.Done:
                    _textSegment = 1;
                    _mediaSegment = 1;
                    _auxSegment = 1;
                    break;
            }
            Emit();
        }

        public void MarkTextComplete()
        {
            _textSegment = 1;
            Emit();
        }

        public void BeginAuxPhase(int totalSteps)
        {
            _textSegment = 1;
            _mediaSegment = _mediaEnabled ? Math.Max(_mediaSegment, 1) : 1;
            _auxTotal = Math.Max(totalSteps, 1);
            _auxStep = 0;
            _auxSegment = 0;
            Emit();
        }

        public void AuxStep()
        {
            _auxStep++;
            _auxSegment = Math.Min(1, _auxStep / (double)_auxTotal);
            Emit();
        }

        public void MarkAuxComplete()
        {
            _auxSegment = 1;
            Emit();
        }

        public void SetMediaFileHint(string? hint)
        {
            CurrentFileHint = hint;
            Emit();
        }

        public void SetParticipantFetch(int offset, int total)
        {
            ParticipantFetchOffset = offset;
            ParticipantFetchTotal = Math.Max(total, 1);
            BioPhase = false;
            Emit();
        }

        public void BeginBiosPhase(int totalUsers)
        {
            BioPhase = true;
            BioStep = 0;
            BioTotal = Math.Max(totalUsers, 1);
            ParticipantFetchTotal = 0;
            Emit();
        }

        public void SetBioProgress(int step, int total)
        {
            BioStep = step;
            BioTotal = Math.Max(total, 1);
            Emit();
        }

        public void SyncMedia(MediaDownloadStats st, int processed, int total, string? hint)
        {
            Phase = ExportWorkPhase.Media;
            _textSegment = 1;
            Photos = st.Photos;
            Videos = st.Videos;
            Gifs = st.Gifs;
            Stickers = st.Stickers;
            Voices = st.Voices;
            Docs = st.Docs;
            Skipped = st.Skipped;
            BytesDownloaded = st.TotalBytes;
            if (total > 0)
            {
                MediaTotal = total;
                MediaProcessed = processed;
                _mediaSegment = Math.Min(1, processed / (double)total);
            }
            else if (processed > 0 && MediaTotal > 0)
            {
                MediaProcessed = processed;
                _mediaSegment = Math.Min(1, processed / (double)MediaTotal);
            }
            else if (Phase == ExportWorkPhase.Media)
                _mediaSegment = 1;
            CurrentFileHint = hint;
            Emit();
        }

        public void Log(string line) => Emit(line);

        /// <summary>Обновить полосу без новой строки в журнале.</summary>
        public void Pulse() => Emit();

        private void Emit(string? logLine = null)
        {
            var (indet, frac) = ComputeProgress();
            var title = Phase switch
            {
                ExportWorkPhase.History => "Загрузка истории сообщений",
                ExportWorkPhase.Participants => "Участники и профили",
                ExportWorkPhase.Html => "Сборка HTML-архива",
                ExportWorkPhase.Media => "Скачивание медиа",
                ExportWorkPhase.Done => "Готово",
                _ => "Экспорт"
            };

            var pStep = BioPhase ? BioStep : ParticipantFetchOffset;
            var pTot = BioPhase ? BioTotal : (ParticipantFetchTotal > 0 ? ParticipantFetchTotal : 1);

            _target.Report(new ExportProgressReport(
                Phase, title, logLine,
                MessagesLoadedCount,
                MediaProcessed, MediaTotal,
                Photos, Videos, Gifs, Stickers, Voices, Docs, Skipped,
                BytesDownloaded,
                CurrentFileHint,
                pStep, pTot,
                indet, frac,
                _textSegment, _mediaSegment, _auxSegment));
        }

        private (bool Indeterminate, double Fraction) ComputeProgress()
        {
            if (Phase == ExportWorkPhase.History)
            {
                if (MessagesLoadedCount <= 0)
                    return (true, OverallFraction());
                _textSegment = Math.Max(_textSegment, Math.Min(0.5, 0.08 + MessagesLoadedCount / 120_000.0 * 0.42));
            }
            else if (Phase == ExportWorkPhase.Participants)
            {
                if (BioPhase && BioTotal > 0)
                    _textSegment = Math.Max(_textSegment, 0.55 + 0.28 * Math.Min(1, BioStep / (double)BioTotal));
                else if (!BioPhase && ParticipantFetchTotal > 0)
                    _textSegment = Math.Max(_textSegment, 0.52 + 0.30 * Math.Min(1, ParticipantFetchOffset / (double)ParticipantFetchTotal));
            }

            return (false, OverallFraction());
        }

        private double OverallFraction()
        {
            var wMedia = _mediaEnabled ? WeightMedia : 0;
            var wAux = _auxEnabled ? WeightAux : 0;
            var wText = WeightText;
            var sum = wText + wMedia + wAux;
            if (sum <= 0) return 0;
            var mediaP = _mediaEnabled ? _mediaSegment : 1;
            var auxP = _auxEnabled ? _auxSegment : 1;
            return (wText * _textSegment + wMedia * mediaP + wAux * auxP) / sum;
        }
    }
}
