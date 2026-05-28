using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TelegaScan.Services;

public enum QueueItemStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// Элемент очереди экспорта с отображением статуса в UI.
/// </summary>
public sealed class ExportQueueItem : INotifyPropertyChanged
{
    private QueueItemStatus _status = QueueItemStatus.Pending;
    private string _statusHint = "";
    private DialogListItem? _chat;

    public ExportQueueItem(DialogListItem chat)
    {
        _chat = chat;
        PeerKey = DialogPeerKey.From(chat);
        Title = chat.Title;
        Subtitle = chat.Subtitle;
    }

    private ExportQueueItem(string peerKey, string title, string subtitle)
    {
        PeerKey = peerKey;
        Title = title;
        Subtitle = subtitle;
    }

    public static ExportQueueItem Restore(string peerKey, string title, string subtitle) =>
        new(peerKey, title, subtitle);

    public string PeerKey { get; private set; }
    public string Title { get; }
    public string Subtitle { get; }
    public int Position { get; set; }

    public string PositionLabel => $"{Position}.";

    public DialogListItem? ChatOrNull => _chat;

    public DialogListItem Chat =>
        _chat ?? throw new InvalidOperationException($"Чат «{Title}» не привязан к списку. Подключитесь к Telegram.");

    public void AttachChat(DialogListItem chat)
    {
        _chat = chat;
        PeerKey = DialogPeerKey.From(chat);
    }

    public bool IsSameDialog(DialogListItem chat)
    {
        if (PeerKey == chat.PeerKey) return true;
        if (DialogPeerKey.TryParse(PeerKey, out var kind, out var peerId))
            return chat.Kind == kind && chat.PeerId == peerId;
        return HasChat && ChatOrNull!.PeerKey == chat.PeerKey;
    }

    public bool HasChat => _chat != null;

    public QueueItemStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(DisplayOrder));
        }
    }

    public string StatusHint
    {
        get => _statusHint;
        set { _statusHint = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); }
    }

    public string StatusLabel => Status switch
    {
        QueueItemStatus.Pending => "ожидает",
        QueueItemStatus.Running => string.IsNullOrEmpty(StatusHint) ? "скачивается…" : StatusHint,
        QueueItemStatus.Done => "готово",
        QueueItemStatus.Failed => string.IsNullOrEmpty(StatusHint) ? "ошибка" : StatusHint,
        QueueItemStatus.Cancelled => "отменено",
        _ => ""
    };

    public bool IsPending => Status == QueueItemStatus.Pending;
    public bool IsRunning => Status == QueueItemStatus.Running;
    public bool IsDone => Status == QueueItemStatus.Done;
    public bool IsFailed => Status == QueueItemStatus.Failed;

    /// <summary>Порядок в списке: сначала скачивается, потом ожидает, в конце готовые.</summary>
    public int DisplayOrder => Status switch
    {
        QueueItemStatus.Running => 0,
        QueueItemStatus.Pending => 1,
        QueueItemStatus.Failed => 2,
        QueueItemStatus.Cancelled => 3,
        QueueItemStatus.Done => 4,
        _ => 5
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
