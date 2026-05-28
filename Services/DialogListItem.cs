using System.ComponentModel;
using System.Runtime.CompilerServices;
using TL;

namespace TelegaScan.Services;

/// <summary>
/// Элемент списка чатов для UI; хранит готовый InputPeer для экспорта.
/// </summary>
public sealed class DialogListItem : INotifyPropertyChanged
{
    private bool _isMarked;
    private int _messageCount = -1;

    public required string Title { get; init; }
    public required InputPeer Peer { get; init; }
    public string Subtitle { get; init; } = "";
    public DialogKind Kind { get; init; }
    public bool IsArchived { get; init; }
    public long PeerId { get; init; }

    /// <summary>Дата последнего сообщения (UTC); MinValue если неизвестна.</summary>
    public DateTime LastMessageUtc { get; init; }

    /// <summary>Число сообщений в чате; -1 пока не загружено.</summary>
    public int MessageCount
    {
        get => _messageCount;
        set
        {
            if (_messageCount == value) return;
            _messageCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CountHint));
        }
    }

    public string CountHint => MessageCount >= 0 ? $" · {MessageCount:N0} сообщ." : "";

    /// <summary>Отмечен для добавления в очередь (чекбокс в списке).</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value) return;
            _isMarked = value;
            OnPropertyChanged();
        }
    }

    public string PeerKey => DialogPeerKey.Create(Kind, PeerId);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
