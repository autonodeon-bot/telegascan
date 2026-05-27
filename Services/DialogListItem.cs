using TL;

namespace TelegaScan.Services;

/// <summary>
/// Элемент списка чатов для UI; хранит готовый InputPeer для экспорта.
/// </summary>
public sealed class DialogListItem
{
    public required string Title { get; init; }
    public required InputPeer Peer { get; init; }
    public string Subtitle { get; init; } = "";
}
