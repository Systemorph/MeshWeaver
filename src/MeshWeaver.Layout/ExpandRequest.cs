using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents an event that occurs when an area is clicked.
/// </summary>
/// <param name="Area">The area that was clicked.</param>
public record ClickedEvent(string Area, string StreamId) : StreamMessage(StreamId)
{
    /// <summary>
    /// Gets or initializes the payload associated with the clicked event.
    /// </summary>
    public object? Payload { get; init; }
}

/// <summary>
/// Represents an event that occurs when a form control loses focus.
/// </summary>
/// <param name="Area">The area that lost focus.</param>
/// <param name="StreamId">The stream identifier.</param>
public record BlurEvent(string Area, string StreamId) : StreamMessage(StreamId)
{
    /// <summary>
    /// Gets or initializes the payload associated with the blur event.
    /// </summary>
    public object? Payload { get; init; }
}

/// <summary>
/// Represents an event that occurs when a dialog is closed.
/// </summary>
/// <param name="Area">The area where the dialog was displayed.</param>
/// <param name="StreamId">The stream identifier.</param>
/// <param name="State">The state indicating how the dialog was closed (OK, Cancel, etc.)</param>
public record CloseDialogEvent(string Area, string StreamId, DialogCloseState State) : StreamMessage(StreamId)
{
    /// <summary>
    /// Gets or initializes the payload associated with the close dialog event.
    /// </summary>
    public object? Payload { get; init; }
}

/// <summary>
/// Represents the different states for closing a dialog.
/// </summary>
public enum DialogCloseState
{
    /// <summary>
    /// Dialog was closed with OK/Accept action.
    /// </summary>
    OK,

    /// <summary>
    /// Dialog was cancelled or dismissed.
    /// </summary>
    Cancel
}

/// <summary>
/// Context provided when a dialog close action is invoked.
/// </summary>
/// <param name="Area">The area where the dialog was displayed.</param>
/// <param name="State">The state indicating how the dialog was closed.</param>
/// <param name="Payload">Optional payload data.</param>
/// <param name="Hub">The message hub for posting messages.</param>
/// <param name="Host">The layout area host.</param>
public record DialogCloseActionContext(string Area, DialogCloseState State, object Payload, IMessageHub Hub, LayoutAreaHost Host);

/// <summary>
/// Represents a navigation request that should be handled by the Blazor portal.
/// </summary>
/// <param name="Uri">The URI to navigate to.</param>
public record NavigationRequest(string Uri)
{
    /// <summary>
    /// Whether to force a full page load.
    /// </summary>
    public bool ForceLoad { get; init; }

    /// <summary>
    /// Whether to replace the current history entry instead of adding a new one.
    /// </summary>
    public bool Replace { get; init; }

    /// <summary>
    /// When "SidePanel", the navigation target is the side panel content path
    /// instead of the main browser location.
    /// </summary>
    public string? Target { get; init; }
}
