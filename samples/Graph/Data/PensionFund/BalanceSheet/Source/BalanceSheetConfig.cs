// <meshweaver>
// Id: BalanceSheetConfig
// DisplayName: Balance Sheet Configuration
// </meshweaver>

using MeshWeaver.BusinessRules;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Content of a balance-sheet report INSTANCE node (e.g.
/// PensionFund/Statement). The views compute everything from the dimension
/// and fact nodes; the instance carries only free commentary.
/// </summary>
public record BalanceSheetReport
{
    /// <summary>Management commentary shown alongside the statement.</summary>
    public string? Commentary { get; init; }
}

/// <summary>
/// Hub configuration applied to every node of type PensionFund/BalanceSheet —
/// the report INSTANCES (like FutuRe/Analysis for FutuRe/GroupAnalysis), not
/// the NodeType definition node. Registers the business-rules scopes of THIS
/// compiled node assembly (the NodeType compiler ran the scope generator over
/// the Source — AddBusinessRules discovers the generated implementations) and
/// wires the balance-sheet views. Referenced from BalanceSheet.json.
/// </summary>
public static class BalanceSheetConfig
{
    public static MessageHubConfiguration ConfigureBalanceSheet(this MessageHubConfiguration config)
        => config
            .WithServices(services => services.AddBusinessRules(typeof(PositionValue).Assembly))
            .WithContentType<BalanceSheetReport>()
            // The mesh queries live in VIRTUAL DATA SOURCES — subscribed once by the
            // framework at hub init, never from inside a view render (which would run
            // them on the grain's activation thread and deadlock the hub). Views read
            // the resulting LOCAL workspace streams via BalanceSheetData.LoadStorage.
            .AddData(data => data
                .WithVirtualDataSource("BalanceSheetModel", vs => vs
                    .WithVirtualType<PositionNode>(workspace => BalanceSheetData.LoadPositions(workspace))
                    .WithVirtualType<YearNode>(workspace => BalanceSheetData.LoadYears(workspace))
                    .WithVirtualType<EntryNode>(workspace => BalanceSheetData.LoadEntries(workspace))))
            .AddDefaultLayoutAreas()
            // Land on the statement itself — without a default area the page falls
            // back to the generic content view, which shows only the commentary.
            .AddLayout(layout => layout.AddBalanceSheetLayoutAreas()
                .WithDefaultArea("BalanceSheetStatement"));
}
