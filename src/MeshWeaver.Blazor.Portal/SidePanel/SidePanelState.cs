namespace MeshWeaver.Blazor.Portal.SidePanel;

/// <summary>
/// Represents the persisted state of the side panel.
/// </summary>
public record SidePanelState
{
    /// <summary>
    /// Whether the side panel is currently shown.
    /// </summary>
    public bool IsVisible { get; init; }

    /// <summary>
    /// The edge the side panel is docked to.
    /// </summary>
    public SidePanelPosition Position { get; init; } = SidePanelPosition.Right;

    /// <summary>
    /// Persisted panel width as a percentage of the main area, or null for the default.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    /// Persisted panel height as a percentage of the main area, or null for the default.
    /// </summary>
    public int? Height { get; init; }
    /// <summary>
    /// The path to the currently active content (e.g., "User/{userId}/Threads/{threadId}" or "{context}/Threads/{threadId}").
    /// </summary>
    public string? ContentPath { get; init; }

    /// <summary>
    /// Display title for the side panel header (e.g., thread name or "New Thread").
    /// </summary>
    public string? Title { get; init; }
}
