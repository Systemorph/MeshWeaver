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
/// Hub configuration for the BalanceSheet node: registers the business-rules
/// scopes of THIS compiled node assembly (the NodeType compiler ran the scope
/// generator over the Source — AddBusinessRules discovers the generated
/// implementations) and wires the balance-sheet views. Referenced from
/// BalanceSheet.json.
/// </summary>
public static class BalanceSheetConfig
{
    public static MessageHubConfiguration ConfigureBalanceSheet(this MessageHubConfiguration config)
        => config
            .WithServices(services => services.AddBusinessRules(typeof(PositionValue).Assembly))
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout.AddBalanceSheetLayoutAreas());
}
