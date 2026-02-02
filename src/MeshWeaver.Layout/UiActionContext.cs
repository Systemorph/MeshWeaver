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

/// <summary>
/// Extension methods for UiActionContext.
/// </summary>
public static class UiActionContextExtensions
{
    /// <summary>
    /// Navigates to the specified URI by posting a NavigationRequest to the portal.
    /// Safe to call from click handlers and other UI action contexts.
    /// </summary>
    /// <param name="context">The UI action context.</param>
    /// <param name="uri">The URI to navigate to.</param>
    /// <param name="forceLoad">Whether to force a full page reload.</param>
    public static void NavigateTo(this UiActionContext context, string uri, bool forceLoad = false)
    {
        context.Host.NavigateTo(uri, forceLoad);
    }
}
