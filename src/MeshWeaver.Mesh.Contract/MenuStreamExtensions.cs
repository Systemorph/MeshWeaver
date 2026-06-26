using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Reactive, renderer-agnostic access to a node's menu — the framework counterpart of
/// <c>hub.GetQuery(...)</c>. The node hub's <c>RenderMenus</c> renderer combines every registered
/// <see cref="INodeMenuProvider"/> / <see cref="NodeMenuItemProvider"/>, applies the viewer's effective
/// permissions, sorts by <see cref="NodeMenuItemDefinition.Order"/>, and writes the resulting
/// <see cref="MenuControl"/> to the <c>$Menu:{context}</c> slot of the layout-area stream — re-emitting
/// whenever a provider's inputs change (most importantly the viewer's permissions). <see cref="GetMenu(ISynchronizationStream{JsonElement}, string?)"/>
/// reads that slot with the SAME stream tech as <see cref="LayoutExtensions.GetControlStream(MeshWeaver.Data.ISynchronizationStream{System.Text.Json.JsonElement}, string)"/> — there is
/// no Blazor-only <c>IMenuItemsProvider</c> to replicate per renderer. Both the Blazor portal and the
/// native MAUI shell consume this one observable.
/// </summary>
public static class MenuStreamExtensions
{
    /// <summary>
    /// The live menu items for <paramref name="context"/> (default <c>null</c> = the root <c>$Menu</c>
    /// slot; named contexts such as <c>"Node"</c>, <c>"Mesh"</c>, <c>"Ai"</c> read <c>$Menu:{context}</c>)
    /// carried in this layout-area stream. Re-emits whenever the node hub re-renders the menu (e.g. a
    /// runtime <c>AccessAssignment</c> grants a role). Yields an empty list while no menu is present.
    /// </summary>
    public static IObservable<IReadOnlyList<NodeMenuItemDefinition>> GetMenu(
        this ISynchronizationStream<JsonElement> stream, string? context = null)
        => stream.GetControlStream<MenuControl>(MenuControl.GetMenuArea(context))
            .Select(menu => menu?.Items ?? (IReadOnlyList<NodeMenuItemDefinition>)[]);

    /// <summary>
    /// Workspace shorthand — opens the node's layout-area stream (shared via the workspace's remote-stream
    /// cache, so it costs nothing extra when that same area is already being rendered) and returns its
    /// <see cref="GetMenu(ISynchronizationStream{JsonElement}, string?)"/>.
    /// </summary>
    public static IObservable<IReadOnlyList<NodeMenuItemDefinition>> GetMenu(
        this IWorkspace workspace, Address address, LayoutAreaReference reference, string? context = null)
        => workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference)!.GetMenu(context);

    /// <summary>
    /// Hub shorthand — the canonical surface for application code, same shape as <c>hub.GetQuery(...)</c>:
    /// a hub-level reactive menu API. Resolves the workspace from the hub and delegates to the workspace
    /// overload.
    /// </summary>
    public static IObservable<IReadOnlyList<NodeMenuItemDefinition>> GetMenu(
        this IMessageHub hub, Address address, LayoutAreaReference reference, string? context = null)
        => hub.GetWorkspace().GetMenu(address, reference, context);
}
