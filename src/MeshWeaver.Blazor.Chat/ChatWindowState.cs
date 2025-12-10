namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Represents the persisted state of the chat window.
/// </summary>
public record ChatWindowState
{
    public bool IsVisible { get; init; }
    public ChatPosition Position { get; init; } = ChatPosition.Right;
    public int? Width { get; init; }
    public int? Height { get; init; }
}
