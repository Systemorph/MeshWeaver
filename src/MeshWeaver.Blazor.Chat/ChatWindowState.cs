namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Represents the persisted state of the side panel.
/// </summary>
public record SidePanelState
{
    public bool IsVisible { get; init; }
    public SidePanelPosition Position { get; init; } = SidePanelPosition.Right;
    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>
    /// The path to the currently active content (e.g., "User/{userId}/Threads/{threadId}" or "{context}/Threads/{threadId}").
    /// </summary>
    public string? ContentPath { get; init; }
}
