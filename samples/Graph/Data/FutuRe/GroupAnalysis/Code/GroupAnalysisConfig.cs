// <meshweaver>
// Id: GroupAnalysisConfig
// DisplayName: Group Analysis Configuration
// </meshweaver>

using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;

/// <summary>
/// Configures the group-level analysis hub: aggregates local BU data
/// via PartitionedHubDataSource and applies transaction mapping rules.
/// </summary>
public static class GroupAnalysisConfig
{
    public static MessageHubConfiguration ConfigureGroupAnalysis(this MessageHubConfiguration config)
        => config
            .WithContentType<AnalysisContent>()
            .AddData(data => data
                .AddPartitionedHubSource<Address>(
                    c => c.WithType<FutuReDataCube>(
                            cube => (Address)("FutuRe/" + cube.BusinessUnit + "/Analysis"))
                        .InitializingPartitions(
                            (Address)"FutuRe/EuropeRe/Analysis",
                            (Address)"FutuRe/AmericasIns/Analysis"))
                .WithVirtualDataSource("ReferenceData", vs => vs
                    .WithVirtualType<AmountType>(
                        workspace => FutuReDataLoader.LoadAmountTypes(workspace))
                    .WithVirtualType<Currency>(
                        workspace => FutuReDataLoader.LoadCurrencies(workspace))
                    .WithVirtualType<Country>(
                        workspace => FutuReDataLoader.LoadCountries(workspace))
                    .WithVirtualType<ExchangeRate>(
                        workspace => FutuReDataLoader.LoadExchangeRates(workspace))
                    .WithVirtualType<BusinessUnit>(
                        workspace => FutuReDataLoader.LoadBusinessUnits(workspace)))
                .WithVirtualDataSource("TransactionMapping", vs => vs
                    .WithVirtualType<TransactionMapping>(
                        workspace => FutuReDataLoader.LoadTransactionMappingsFromNodes(workspace)))
                .WithVirtualDataSource("LineOfBusiness", vs => vs
                    .WithVirtualType<LineOfBusiness>(
                        workspace => FutuReDataLoader.LoadLinesOfBusinessFromNodes(workspace))))
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .AddProfitabilityLayoutAreas()
                .WithView("GroupProfitabilityDashboard", ProfitabilityLayoutAreas.GroupProfitabilityDashboard));
}
