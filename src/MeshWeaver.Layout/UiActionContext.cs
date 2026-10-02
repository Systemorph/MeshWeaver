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
public record UiActionContext(string Area, object? Payload, IMessageHub Hub, LayoutAreaHost Host)
{
    /// <summary>
    /// The row this action was raised from, when its control is declared inside a bound row template
    /// (a <c>BindMany</c> list, a data grid's template column) — the row as the client rendered it.
    /// Null for a control outside any row. Read the value with <c>RowAs&lt;T&gt;()</c> and a node
    /// row's path with <see cref="UiActionContextExtensions.RowPath"/>; see <see cref="RowContext"/>
    /// for why it is the rendered row and never a re-resolved index.
    /// </summary>
    public RowContext? Row { get; init; }
}

/// <summary>
/// Extension methods for UiActionContext.
/// </summary>
public static class UiActionContextExtensions
{
    /// <summary>
    /// The mesh path of the row this action was raised from (<see cref="RowContext.NodePath"/>), or
    /// null when the control is not in a row or the row is not a node.
    /// </summary>
    /// <param name="context">The UI action context.</param>
    /// <returns>The row's node path, or null.</returns>
    public static string? RowPath(this UiActionContext context) => context.Row?.NodePath();

    /// <summary>
    /// Navigates to the specified URI by posting a NavigationRequest to the portal.
    /// Safe to call from click handlers and other UI action contexts.
    /// </summary>
    /// <param name="context">The UI action context.</param>
    /// <param name="uri">The URI to navigate to.</param>
    /// <param name="forceLoad">Whether to force a full page reload.</param>
    /// <param name="replace">Whether to replace the current history entry instead of adding a new one.</param>
    public static void NavigateTo(this UiActionContext context, string uri, bool forceLoad = false, bool replace = false)
    {
        context.Host.NavigateTo(uri, forceLoad, replace);
    }

    /// <summary>
    /// Opens the URI in the portal's side panel, leaving the page the viewer is on in place —
    /// see <see cref="Composition.LayoutAreaHost.NavigateToSidePanel"/> for when to prefer this
    /// over <see cref="NavigateTo"/>.
    /// </summary>
    /// <param name="context">The action context of the click.</param>
    /// <param name="uri">The URI to open in the side panel.</param>
    public static void NavigateToSidePanel(this UiActionContext context, string uri)
    {
        context.Host.NavigateToSidePanel(uri);
    }
}
