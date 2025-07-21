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
