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

    public ExportQueueItem(DialogListItem chat)
    {
        Chat = chat;
        Title = chat.Title;
        Subtitle = chat.Subtitle;
    }

    public DialogListItem Chat { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public int Position { get; set; }

    public string PositionLabel => $"{Position}.";

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
