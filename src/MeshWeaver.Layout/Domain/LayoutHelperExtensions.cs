using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Domain;

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
    public static async Task<EntityStoreAndUpdates> ConfigBasedRenderer<TControl>(this LayoutAreaHost host,
        RenderingContext context,
        EntityStore store,
        string area,
        Func<TControl> factory,
        Func<TControl, LayoutAreaHost, RenderingContext, Task<TControl>> config)
        where TControl : UiControl
    {
        var menu = store.GetLayoutArea<TControl>(area) ?? factory();
        menu = await config(menu, host, context);
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

    public static int DataHashCode(object data)
    {
        if (data is null)
            return 0;
        if (data is IEnumerable<object> e)
            return e.Aggregate(0, (i, y) => i ^ y.GetHashCode());
        return data.GetHashCode();
    }
}
