using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.SidePanel;

/// <summary>
/// Service to manage and persist side panel state across circuit reconnections.
/// Uses .NET 10's [PersistentState] attribute for automatic state persistence.
/// </summary>
public class SidePanelStateService
{
    /// <summary>
    /// The persisted side panel state. This will survive circuit disconnections.
    /// </summary>
    [PersistentState]
    public SidePanelState State { get; set; } = new();

    /// <summary>
    /// Event raised when the state changes (visibility or position only, not size).
    /// </summary>
    public event Action? OnStateChanged;

    public bool IsVisible => State.IsVisible;
    public SidePanelPosition Position => State.Position;
    public int? Width => State.Width;
    public int? Height => State.Height;
    public string? ContentPath => State.ContentPath;

    public void SetVisible(bool visible)
    {
        if (State.IsVisible != visible)
        {
            State = State with { IsVisible = visible };
            NotifyStateChanged();
        }
    }

    public void SetPosition(SidePanelPosition position)
    {
        if (State.Position != position)
        {
            State = State with { Position = position };
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Updates the size without triggering UI re-render.
    /// Size changes are purely for persistence - JS handles the actual resize.
    /// </summary>
    public void SetSize(int? width, int? height)
    {
        if (State.Width != width || State.Height != height)
        {
            State = State with { Width = width, Height = height };
        }
    }

    public void Toggle()
    {
        State = State with { IsVisible = !State.IsVisible };
        NotifyStateChanged();
    }

    /// <summary>
    /// Sets the current content path being viewed/edited.
    /// </summary>
    public void SetContentPath(string? contentPath)
    {
        if (State.ContentPath != contentPath)
        {
            State = State with { ContentPath = contentPath };
        }
    }

    /// <summary>
    /// Opens the side panel and sets the active content path.
    /// </summary>
    public void OpenWithContent(string contentPath)
    {
        State = State with { IsVisible = true, ContentPath = contentPath };
        NotifyStateChanged();
    }

    public void ResetSize()
    {
        State = State with { Width = null, Height = null };
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
