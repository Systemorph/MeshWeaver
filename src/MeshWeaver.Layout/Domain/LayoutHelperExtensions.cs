using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Domain;

/// <summary>
/// Extension helpers for layout rendering: stream-based view factories, config-based renderer setup,
/// and data equality/hash utilities used by the rendering infrastructure.
/// </summary>
public static class LayoutHelperExtensions
{
    /// <summary>
    /// Creates an observable view from a data stream with a loading placeholder.
    /// </summary>
    /// <typeparam name="T">The type of entities in the stream.</typeparam>
    /// <param name="host">The layout area host.</param>
    /// <param name="viewFactory">Factory function to create the view from the data.</param>
    /// <param name="loadingTitle">Title to display while loading.</param>
    /// <returns>An observable UI control that updates when data changes.</returns>
    public static IObservable<UiControl?> StreamView<T>(
        this LayoutAreaHost host,
        Func<IReadOnlyCollection<T>, LayoutAreaHost, UiControl?> viewFactory,
        string loadingTitle)
    {
        return host.Workspace
            .GetStream<T>()!
            .Select(items => viewFactory(items ?? Array.Empty<T>(), host))
            .StartWith(Controls.Markdown($"# {loadingTitle}\n\n*Loading {loadingTitle.ToLower()}...*"));
    }

    /// <summary>
    /// Creates an observable view from a data stream with a custom loading control.
    /// </summary>
    /// <typeparam name="T">The type of entities in the stream.</typeparam>
    /// <param name="host">The layout area host.</param>
    /// <param name="viewFactory">Factory function to create the view from the data.</param>
    /// <param name="loadingControl">Control to display while loading.</param>
    /// <returns>An observable UI control that updates when data changes.</returns>
    public static IObservable<UiControl?> StreamView<T>(
        this LayoutAreaHost host,
        Func<IReadOnlyCollection<T>, LayoutAreaHost, UiControl?> viewFactory,
        UiControl loadingControl)
    {
        return host.Workspace
            .GetStream<T>()!
            .Select(items => viewFactory(items ?? Array.Empty<T>(), host))
            .StartWith(loadingControl);
    }

    /// <summary>
    /// Creates an observable view from a data stream without passing the host to the factory.
    /// </summary>
    /// <typeparam name="T">The type of entities in the stream.</typeparam>
    /// <param name="host">The layout area host.</param>
    /// <param name="viewFactory">Factory function to create the view from the data.</param>
    /// <param name="loadingTitle">Title to display while loading.</param>
    /// <returns>An observable UI control that updates when data changes.</returns>
    public static IObservable<UiControl?> StreamView<T>(
        this LayoutAreaHost host,
        Func<IReadOnlyCollection<T>, UiControl?> viewFactory,
        string loadingTitle)
    {
        return host.Workspace
            .GetStream<T>()!
            .Select(items => viewFactory(items ?? Array.Empty<T>()))
            .StartWith(Controls.Markdown($"# {loadingTitle}\n\n*Loading {loadingTitle.ToLower()}...*"));
    }

    /// <summary>
    /// Retrieves (or creates via <paramref name="factory"/>) the <typeparamref name="TControl"/> stored
    /// under <paramref name="area"/> in <paramref name="store"/>, applies <paramref name="config"/>, then
    /// renders it — the canonical pattern for singleton config-controlled areas such as the nav menu.
    /// </summary>
    /// <typeparam name="TControl">The UI control type managed for the area.</typeparam>
    /// <param name="host">The layout area host owning the rendering stream.</param>
    /// <param name="context">The current rendering context.</param>
    /// <param name="store">The entity store that may already contain the control.</param>
    /// <param name="area">The area name used as the key in the store.</param>
    /// <param name="factory">Creates a default control when none is found in the store.</param>
    /// <param name="config">Applies caller-supplied configuration to the retrieved or created control.</param>
    /// <returns>The updated store and incremental change set after rendering the configured control.</returns>
    public static EntityStoreAndUpdates ConfigBasedRenderer<TControl>(this LayoutAreaHost host,
        RenderingContext context,
        EntityStore store,
        string area,
        Func<TControl> factory,
        Func<TControl, LayoutAreaHost, RenderingContext, TControl> config)
        where TControl : UiControl
    {
        var menu = store.GetLayoutArea<TControl>(area) ?? factory();
        menu = config(menu, host, context);
        return host.RenderArea(
                context with { Area = area }, menu, store)
            ;
    }
    internal static bool DataEquality(object data, object otherData)
    {
        if (data is null)
            return otherData is null;

        if (data is IEnumerable<object> e)
            return otherData is IEnumerable<object> e2 && e.SequenceEqual(e2, JsonObjectEqualityComparer.Instance);
        return JsonObjectEqualityComparer.Instance.Equals(data, otherData);
    }

    /// <summary>
    /// Computes a stable hash code for a data value suitable for change-detection in the rendering pipeline.
    /// Sequences are hashed by XOR-folding element hash codes; null returns 0.
    /// </summary>
    /// <param name="data">The data value to hash; may be null or an <see cref="IEnumerable{T}"/> of objects.</param>
    /// <returns>An integer hash code.</returns>
    public static int DataHashCode(object data)
    {
        if (data is null)
            return 0;
        if (data is IEnumerable<object> e)
            return e.Aggregate(0, (i, y) => i ^ y.GetHashCode());
        return data.GetHashCode();
    }
}
