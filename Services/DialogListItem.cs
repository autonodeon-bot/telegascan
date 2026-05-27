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

    public required string Title { get; init; }
    public required InputPeer Peer { get; init; }
    public string Subtitle { get; init; } = "";

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

    public string PeerKey => Peer?.ToString() ?? Title;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
