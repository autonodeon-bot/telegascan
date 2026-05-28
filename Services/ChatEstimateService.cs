using TL;
using WTelegram;

namespace TelegaScan.Services;

public sealed class ChatEstimateResult
{
    public int MessageCount { get; init; }
    public int MediaCount { get; init; }
    public long EstimatedBytes { get; init; }
    public int NewSinceExport { get; init; }
}

public static class ChatEstimateService
{
    public static async Task<ChatEstimateResult> EstimateAsync(
        Client client,
        DialogListItem chat,
        string folderRoot,
        ExportOptions opts,
        CancellationToken ct)
    {
        var outDir = ExportPathHelper.ResolveChatOutputDir(folderRoot, chat, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var state = IncrementalStateService.Load(outDir);
        var lastId = opts.Incremental ? state.LastMessageId : 0;

        var (sorted, _, _) = await Task.Run(() =>
            ChatExportService.LoadAllMessagesInternalAsync(client, chat.Peer, opts, null, ct), ct).ConfigureAwait(false);

        var filtered = sorted;
        if (opts.Incremental && lastId > 0)
            filtered = sorted.Where(m => m.ID > lastId).ToList();

        long bytes = 0;
        var media = 0;
        foreach (var m in filtered)
        {
            if (m is not Message msg || msg.media is null) continue;
            media++;
            bytes += EstimateMediaBytes(msg.media);
        }

        return new ChatEstimateResult
        {
            MessageCount = filtered.Count,
            MediaCount = media,
            EstimatedBytes = bytes,
            NewSinceExport = opts.Incremental && lastId > 0 ? filtered.Count : 0
        };
    }

    private static long EstimateMediaBytes(MessageMedia media) => media switch
    {
        MessageMediaDocument doc when doc.document is Document d => d.size,
        MessageMediaPhoto => 400_000,
        _ => 100_000
    };
}
