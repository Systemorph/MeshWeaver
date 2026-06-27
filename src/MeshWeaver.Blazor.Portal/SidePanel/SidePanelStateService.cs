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

    /// <summary>
    /// Event raised when a menu action is requested (e.g., "New", "Resume").
    /// ThreadChatView subscribes to handle mode changes.
    /// </summary>
    public event Action<string>? OnActionRequested;

    /// <summary>
    /// Whether the side panel is currently shown.
    /// </summary>
    public bool IsVisible => State.IsVisible;

    /// <summary>
    /// The edge the side panel is docked to.
    /// </summary>
    public SidePanelPosition Position => State.Position;

    /// <summary>
    /// Persisted panel width, or null for the default.
    /// </summary>
    public int? Width => State.Width;

    /// <summary>
    /// Persisted panel height, or null for the default.
    /// </summary>
    public int? Height => State.Height;

    /// <summary>
    /// Path of the content currently shown in the panel, or null when empty.
    /// </summary>
    public string? ContentPath => State.ContentPath;

    /// <summary>
    /// Display title shown in the panel header.
    /// </summary>
    public string? Title => State.Title;

    /// <summary>
    /// Sets the panel visibility, notifying listeners only when it actually changes.
    /// </summary>
    /// <param name="visible">True to show the panel, false to hide it.</param>
    public void SetVisible(bool visible)
    {
        if (State.IsVisible != visible)
        {
            State = State with { IsVisible = visible };
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Sets the panel docking position, notifying listeners only when it actually changes.
    /// </summary>
    /// <param name="position">The edge to dock the panel to.</param>
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

    /// <summary>
    /// Flips the panel visibility and notifies listeners.
    /// </summary>
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
            NotifyStateChanged();
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

    /// <summary>
    /// Sets the display title for the side panel header.
    /// </summary>
    public void SetTitle(string? title)
    {
        if (State.Title != title)
        {
            State = State with { Title = title };
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Requests a named action (e.g., "New", "Resume") to be handled by the active content component.
    /// </summary>
    public void RequestAction(string action) => OnActionRequested?.Invoke(action);

    /// <summary>
    /// A one-shot draft to seed the NEXT new-chat composer with — e.g. "new thread from this cell"
    /// prefills the composer with the cell's text. Transient (NOT persisted): the side-panel chat
    /// consumes + clears it on the next "New" action / its init. <see cref="ConsumePendingComposerDraft"/>.
    /// </summary>
    public string? PendingComposerDraft { get; private set; }

    /// <summary>
    /// Opens the chat panel for a NEW thread, seeding the composer with <paramref name="draft"/>. Shows
    /// the panel, clears any open thread (so the new-chat composer renders), stashes the draft, then
    /// signals "New" so a mounted composer resets + picks the draft up. A composer mounting AFTER this
    /// (panel was closed) reads the draft on its own init — so the seed survives the open race.
    /// </summary>
    public void OpenNewThreadWithDraft(string? draft)
    {
        PendingComposerDraft = draft;
        State = State with { IsVisible = true, ContentPath = null, Title = null };
        NotifyStateChanged();
        RequestAction("New");
    }

    /// <summary>Takes and clears the pending composer draft (one-shot — returns null after the first read).</summary>
    public string? ConsumePendingComposerDraft()
    {
        var d = PendingComposerDraft;
        PendingComposerDraft = null;
        return d;
    }

    /// <summary>
    /// Clears the persisted width and height so the panel reverts to its default size.
    /// </summary>
    public void ResetSize()
    {
        State = State with { Width = null, Height = null };
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
