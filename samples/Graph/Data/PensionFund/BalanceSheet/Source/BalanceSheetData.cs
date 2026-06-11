// <meshweaver>
// Id: BalanceSheetData
// DisplayName: Balance Sheet Data Loader
// </meshweaver>

using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Loads the balance-sheet model from the mesh: Position dimension nodes,
/// Year dimension nodes, and BalanceSheetEntry fact nodes — everything is a
/// mesh node, so loading is querying by the dimension/fact NodeType paths.
/// </summary>
public static class BalanceSheetData
{
    /// <summary>
    /// Live stream of Position nodes: content keyed by node path, plus the
    /// display order — which lives on the MESH NODE (<c>MeshNode.Order</c>),
    /// not in the content.
    /// </summary>
    public static IObservable<ImmutableList<(string Path, int Order, Position Content)>> LoadPositions(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:PensionFund/Position namespace:PensionFund/Position state:Active"))
            .Select(change => change.Items
                .Select(n => (n.Path, Order: n.Order ?? 0, Content: n.ContentAs<Position>(workspace.Hub.JsonSerializerOptions)))
                .Where(x => x.Content is not null)
                .Select(x => (x.Path, x.Order, Content: x.Content!))
                .OrderBy(x => x.Order).ThenBy(x => x.Path)
                .ToImmutableList());

    /// <summary>Live stream of Year node paths, ascending by year value.</summary>
    public static IObservable<ImmutableArray<string>> LoadYears(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:PensionFund/Year namespace:PensionFund/Year state:Active"))
            .Select(change => change.Items
                .Select(n => (n.Path, Content: n.ContentAs<Year>(workspace.Hub.JsonSerializerOptions)))
                .Where(x => x.Content is not null)
                .OrderBy(x => x.Content!.Value)
                .Select(x => x.Path)
                .ToImmutableArray());

    /// <summary>Live stream of atomic fact amounts keyed by (position, year) paths.</summary>
    public static IObservable<ImmutableDictionary<(string, string), double>> LoadAmounts(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:PensionFund/BalanceSheetEntry namespace:PensionFund/BalanceSheetEntry state:Active"))
            .Select(change => change.Items
                .Select(n => n.ContentAs<BalanceSheetEntry>(workspace.Hub.JsonSerializerOptions))
                .Where(e => e is not null && e.Position.Length > 0 && e.Year.Length > 0)
                .ToImmutableDictionary(e => (e!.Position, e.Year), e => e!.Amount));

    /// <summary>
    /// The combined storage stream every scope evaluation runs against.
    /// Re-emits whenever a dimension or fact node changes — edit an entry in
    /// the GUI and every computed position re-evaluates.
    /// </summary>
    public static IObservable<BalanceSheetStorage> LoadStorage(IWorkspace workspace)
        => LoadPositions(workspace).CombineLatest(
            LoadYears(workspace),
            LoadAmounts(workspace),
            (positions, years, amounts) => new BalanceSheetStorage
            {
                Positions = positions.ToImmutableDictionary(x => x.Path, x => x.Content),
                OrderedPositions = positions.Select(x => x.Path).ToImmutableList(),
                Years = years,
                Amounts = amounts,
            });
}
