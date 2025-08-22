using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents the context for a UI action, including the area, payload, message hub, and layout area host.
/// </summary>
/// <param name="Area">The area where the UI action is performed.</param>
/// <param name="Payload">The payload associated with the UI action.</param>
/// <param name="Hub">The message hub for handling messages related to the UI action.</param>
/// <param name="Host">The layout area host associated with the UI action.</param>
public record UiActionContext(string Area, object? Payload, IMessageHub Hub, LayoutAreaHost Host);
