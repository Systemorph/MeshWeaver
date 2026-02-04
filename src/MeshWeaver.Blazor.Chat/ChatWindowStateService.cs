using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Service to manage and persist chat window state across circuit reconnections.
/// Uses .NET 10's [PersistentState] attribute for automatic state persistence.
/// </summary>
public class ChatWindowStateService
{
    /// <summary>
    /// The persisted chat window state. This will survive circuit disconnections.
    /// </summary>
    [PersistentState]
    public ChatWindowState State { get; set; } = new();

    /// <summary>
    /// Event raised when the state changes (visibility or position only, not size).
    /// </summary>
    public event Action? OnStateChanged;

    public bool IsVisible => State.IsVisible;
    public ChatPosition Position => State.Position;
    public int? Width => State.Width;
    public int? Height => State.Height;
    public string? CurrentThreadPath => State.CurrentThreadPath;

    public void SetVisible(bool visible)
    {
        if (State.IsVisible != visible)
        {
            State = State with { IsVisible = visible };
            NotifyStateChanged();
        }
    }

    public void SetPosition(ChatPosition position)
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
            // Don't notify - size changes are persisted but don't need UI updates
        }
    }

    public void Toggle()
    {
        State = State with { IsVisible = !State.IsVisible };
        NotifyStateChanged();
    }

    /// <summary>
    /// Sets the current thread path being viewed/edited.
    /// </summary>
    public void SetCurrentThread(string? threadPath)
    {
        if (State.CurrentThreadPath != threadPath)
        {
            State = State with { CurrentThreadPath = threadPath };
            // Don't notify for thread changes - this is internal state
        }
    }

    /// <summary>
    /// Opens the side panel and sets the active thread.
    /// </summary>
    public void OpenSidePanelWithThread(string threadPath)
    {
        State = State with { IsVisible = true, CurrentThreadPath = threadPath };
        NotifyStateChanged();
    }

    public void ResetSize()
    {
        State = State with { Width = null, Height = null };
        // Don't notify - size reset is handled by JS
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
