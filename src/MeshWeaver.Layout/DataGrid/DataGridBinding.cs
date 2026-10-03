using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.DataGrid;

/// <summary>
/// A data grid as a TEMPLATE whose rows are FED (Doc/GUI/DataBinding → "Templates first, data
/// later"). The grid is returned at once, bound by pointer to <c>/data/{id}</c>; it shows the
/// grid's loading shape until the first rows arrive, then every later emission of the rows reaches
/// the same grid. An empty or failed feed reaches the grid's bound empty content.
/// </summary>
public static class DataGridBinding
{
    /// <summary>
    /// Binds <paramref name="rows"/> to a <see cref="DataGridControl"/> declared now. The feed is
    /// subscribed when the grid is BUILT into its area (the seam <see cref="Template"/>'s binders
    /// use), lives as long as that area, and writes, in order: the loading state, then each row set.
    /// Add columns, a click action, and so on to the returned control as usual.
    /// </summary>
    /// <typeparam name="TRow">A plain row record; its camelCase property names are the column properties.</typeparam>
    /// <param name="rows">The row sets, e.g. a live query projected to rows.</param>
    /// <param name="id">The data id the rows live under — stable per grid within the area.</param>
    /// <param name="emptyText">Shown when a row set is empty.</param>
    /// <param name="failedText">Shown when the feed fails; receives the failure's message. The
    /// failure is also logged — it is reported, never swallowed.</param>
    /// <returns>The grid template.</returns>
    public static DataGridControl BindGrid<TRow>(
        this IObservable<IEnumerable<TRow>> rows,
        string id,
        string emptyText,
        Func<string, string> failedText)
    {
        var loadingId = LoadingId(id);
        var emptyId = EmptyId(id);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithLoading(new JsonPointerReference(LayoutAreaReference.GetDataPointer(loadingId)))
            .WithEmptyContent(new JsonPointerReference(LayoutAreaReference.GetDataPointer(emptyId)))
            .WithBuildup((host, context, store) =>
            {
                // ONE ordered subscription: the loading state first, so it can never overwrite a
                // row set that arrived first.
                host.RegisterForDisposal(context.Area, rows
                    .Select(set => (Rows: set.ToImmutableList(), Loading: false, Empty: emptyText))
                    .StartWith((Rows: ImmutableList<TRow>.Empty, Loading: true, Empty: ""))
                    .Subscribe(
                        next =>
                        {
                            host.UpdateData(id, next.Rows);
                            host.UpdateData(emptyId, next.Empty);
                            host.UpdateData(loadingId, next.Loading);
                        },
                        ex =>
                        {
                            host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                                .CreateLogger(typeof(DataGridBinding))
                                .LogWarning(ex, "Grid {Id} in area {Area}: its rows could not be read", id, context.Area);
                            host.UpdateData(id, ImmutableList<TRow>.Empty);
                            host.UpdateData(emptyId, failedText(ex.Message));
                            host.UpdateData(loadingId, false);
                        }));
                return new(store, [], null);
            });
    }

    /// <summary>The data id of a fed grid's loading flag (true until the first row set).</summary>
    /// <param name="id">The grid's data id.</param>
    /// <returns>The flag's data id.</returns>
    public static string LoadingId(string id) => $"{id}_loading";

    /// <summary>The data id of a fed grid's empty text (or failure text).</summary>
    /// <param name="id">The grid's data id.</param>
    /// <returns>The text's data id.</returns>
    public static string EmptyId(string id) => $"{id}_empty";
}
