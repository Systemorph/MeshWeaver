// <meshweaver>
// Id: BalanceSheetData
// DisplayName: Balance Sheet Data Loader
// </meshweaver>

using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Workspace projection of a Position dimension node: the content plus what
/// lives on the MESH NODE itself — the path (identity) and the display order
/// (<c>MeshNode.Order</c>).
/// </summary>
public record PositionNode
{
    [Key]
    public string Path { get; init; } = string.Empty;
    public int Order { get; init; }
    public Position Content { get; init; } = null!;
}

/// <summary>Workspace projection of a Year dimension node.</summary>
public record YearNode
{
    [Key]
    public string Path { get; init; } = string.Empty;
    public Year Content { get; init; } = null!;
}

/// <summary>Workspace projection of a BalanceSheetEntry fact node.</summary>
public record EntryNode
{
    [Key]
    public string Path { get; init; } = string.Empty;
    public BalanceSheetEntry Content { get; init; } = null!;
}

/// <summary>
/// Loads the balance-sheet model from the mesh: Position dimension nodes,
/// Year dimension nodes, and BalanceSheetEntry fact nodes — everything is a
/// mesh node, so loading is querying by the dimension/fact NodeType paths.
///
/// The mesh queries live HERE, as virtual data-source loaders registered in
/// <c>ConfigureBalanceSheet</c> via <c>WithVirtualDataSource</c> — the
/// framework's data plumbing subscribes them ONCE at hub initialization on a
/// managed scheduler. Views never subscribe <c>IMeshService.Query</c>
/// themselves: backend rendering composes only the hub's OWN workspace
/// streams (see AsynchronousCalls — a query subscription inside a layout-area
/// render runs on the grain's activation thread and deadlocks the hub).
/// </summary>
public static class BalanceSheetData
{
    /// <summary>Virtual-source loader: live Position nodes projected to <see cref="PositionNode"/>.</summary>
    public static IObservable<IEnumerable<PositionNode>> LoadPositions(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:PensionFund/Position namespace:PensionFund/Position state:Active"))
            .Select(change => change.Items
                .Select(n => new PositionNode
                {
                    Path = n.Path,
                    Order = n.Order ?? 0,
                    Content = n.ContentAs<Position>(workspace.Hub.JsonSerializerOptions)!,
                })
                .Where(x => x.Content is not null)
                .ToImmutableList()
                .AsEnumerable());

    /// <summary>Virtual-source loader: live Year nodes projected to <see cref="YearNode"/>.</summary>
    public static IObservable<IEnumerable<YearNode>> LoadYears(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:PensionFund/Year namespace:PensionFund/Year state:Active"))
            .Select(change => change.Items
                .Select(n => new YearNode
                {
                    Path = n.Path,
                    Content = n.ContentAs<Year>(workspace.Hub.JsonSerializerOptions)!,
                })
                .Where(x => x.Content is not null)
                .ToImmutableList()
                .AsEnumerable());

    /// <summary>Virtual-source loader: live fact nodes projected to <see cref="EntryNode"/>.</summary>
    public static IObservable<IEnumerable<EntryNode>> LoadEntries(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:PensionFund/BalanceSheetEntry namespace:PensionFund/BalanceSheetEntry state:Active"))
            .Select(change => change.Items
                .Select(n => new EntryNode
                {
                    Path = n.Path,
                    Content = n.ContentAs<BalanceSheetEntry>(workspace.Hub.JsonSerializerOptions)!,
                })
                .Where(x => x.Content is not null
                            && x.Content.Position.Length > 0
                            && x.Content.Year.Length > 0)
                .ToImmutableList()
                .AsEnumerable());

    /// <summary>
    /// The combined storage stream every scope evaluation runs against.
    /// Composed ONLY from the hub's own workspace streams (fed by the virtual
    /// data sources above) — local, render-safe, no cross-hub subscription.
    /// Re-emits whenever a dimension or fact node changes — edit an entry in
    /// the GUI and every computed position re-evaluates.
    /// </summary>
    public static IObservable<BalanceSheetStorage> LoadStorage(IWorkspace workspace)
    {
        var positions = (workspace.GetStream<PositionNode>()
                         ?? Observable.Return<IReadOnlyCollection<PositionNode>?>(null))
            .Select(d => (d?.AsEnumerable() ?? Enumerable.Empty<PositionNode>())
                .OrderBy(x => x.Order).ThenBy(x => x.Path)
                .ToImmutableList());

        var years = (workspace.GetStream<YearNode>()
                     ?? Observable.Return<IReadOnlyCollection<YearNode>?>(null))
            .Select(d => (d?.AsEnumerable() ?? Enumerable.Empty<YearNode>())
                .OrderBy(x => x.Content.Value)
                .Select(x => x.Path)
                .ToImmutableArray());

        var entries = (workspace.GetStream<EntryNode>()
                       ?? Observable.Return<IReadOnlyCollection<EntryNode>?>(null))
            .Select(d => (d?.AsEnumerable() ?? Enumerable.Empty<EntryNode>())
                .ToImmutableDictionary(
                    x => (x.Content.Position, x.Content.Year),
                    x => x.Content.Amount));

        return positions.CombineLatest(years, entries,
            (ps, ys, amounts) => new BalanceSheetStorage
            {
                Positions = ps.ToImmutableDictionary(x => x.Path, x => x.Content),
                OrderedPositions = ps.Select(x => x.Path).ToImmutableList(),
                Years = ys,
                Amounts = amounts,
            });
    }
}
