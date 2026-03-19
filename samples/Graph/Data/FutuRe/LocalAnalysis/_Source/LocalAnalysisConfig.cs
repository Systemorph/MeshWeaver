// <meshweaver>
// Id: LocalAnalysisConfig
// DisplayName: Local Analysis Configuration
// </meshweaver>

using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;

/// <summary>
/// Configures a local business unit analysis hub: loads CSV data
/// and enriches with local line of business display names.
/// Registers the same reference/mapping data sources used by group analysis
/// (exchange rates, business units, transaction mappings, lines of business)
/// so local views have access to all dimensions needed for profitability reporting.
/// </summary>
public static class LocalAnalysisConfig
{
    public static MessageHubConfiguration ConfigureLocalAnalysis(this MessageHubConfiguration config)
        => config
            .WithContentType<AnalysisContent>()
            .AddData(data => data
                .WithVirtualDataSource("ReferenceData", vs => vs
                    .WithVirtualType<AmountType>(
                        workspace => FutuReDataLoader.LoadAmountTypes(workspace))
                    .WithVirtualType<ExchangeRate>(
                        workspace => FutuReDataLoader.LoadExchangeRates(workspace))
                    .WithVirtualType<BusinessUnit>(
                        workspace => FutuReDataLoader.LoadBusinessUnits(workspace)))
                .WithVirtualDataSource("TransactionMapping", vs => vs
                    .WithVirtualType<TransactionMapping>(
                        workspace => FutuReDataLoader.LoadTransactionMappingsFromNodes(workspace)))
                .WithVirtualDataSource("LineOfBusiness", vs => vs
                    .WithVirtualType<LineOfBusiness>(
                        workspace => FutuReDataLoader.LoadLinesOfBusinessFromNodes(workspace)))
                .WithVirtualDataSource("LocalData", vs => vs
                    .WithVirtualType<FutuReDataCube>(
                        workspace => FutuReDataLoader.LoadLocalDataCube(workspace))))
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout.AddProfitabilityLayoutAreas());
}
