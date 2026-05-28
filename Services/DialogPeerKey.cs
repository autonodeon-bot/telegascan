namespace TelegaScan.Services;

/// <summary>Стабильный ключ диалога для очереди и профилей (не зависит от InputPeer.ToString()).</summary>
public static class DialogPeerKey
{
    private const string Prefix = "peer:";

    public static string Create(DialogKind kind, long peerId) => $"{Prefix}{(int)kind}:{peerId}";

    public static string From(DialogListItem chat) => Create(chat.Kind, chat.PeerId);

    public static bool TryParse(string key, out DialogKind kind, out long peerId)
    {
        kind = default;
        peerId = 0;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var body = key[Prefix.Length..];
        var sep = body.IndexOf(':');
        if (sep <= 0) return false;
        if (!int.TryParse(body[..sep], out var kindInt)) return false;
        if (!long.TryParse(body[(sep + 1)..], out peerId)) return false;
        kind = (DialogKind)kindInt;
        return true;
    }

    /// <summary>Найти чат по новому или старому (legacy) ключу из сохранённой очереди.</summary>
    public static DialogListItem? FindChat(IEnumerable<DialogListItem> chats, string peerKey, string? title = null)
    {
        var list = chats as IList<DialogListItem> ?? chats.ToList();

        var exact = list.FirstOrDefault(c => c.PeerKey == peerKey);
        if (exact != null) return exact;

        if (TryParse(peerKey, out var kind, out var peerId))
        {
            var byId = list.FirstOrDefault(c => c.Kind == kind && c.PeerId == peerId);
            if (byId != null) return byId;
        }

        // Старые сохранения: Peer.ToString() вроде "InputPeerUser"
        if (peerKey.Contains("InputPeer", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(title))
        {
            var byTitle = list.Where(c => c.Title == title).ToList();
            if (byTitle.Count == 1) return byTitle[0];
        }

        return null;
    }
}
