using System.IO;
using TL;

namespace TelegaScan.Services;

public static class ExportPathHelper
{
    public static string SanitizeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "chat" : n.Trim();
    }

    public static long GetPeerId(InputPeer peer) => peer switch
    {
        InputPeerUser u => u.user_id,
        InputPeerChat c => c.chat_id,
        InputPeerChannel ch => ch.channel_id,
        _ => 0
    };

    /// <summary>Уникальная папка чата: при коллизии имён добавляется id.</summary>
    public static string ResolveChatOutputDir(string folderRoot, DialogListItem chat, ISet<string> usedFolderNames)
    {
        var baseName = SanitizeName(chat.Title);
        var key = baseName.ToLowerInvariant();
        if (usedFolderNames.Add(key))
            return Path.Combine(folderRoot, baseName);

        var withId = $"{baseName}_{GetPeerId(chat.Peer)}";
        var key2 = withId.ToLowerInvariant();
        var suffix = 2;
        var candidate = withId;
        while (!usedFolderNames.Add(key2))
        {
            candidate = $"{withId}_{suffix++}";
            key2 = candidate.ToLowerInvariant();
        }
        return Path.Combine(folderRoot, candidate);
    }
}
