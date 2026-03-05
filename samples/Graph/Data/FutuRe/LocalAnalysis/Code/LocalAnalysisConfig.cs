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
/// </summary>
public static class LocalAnalysisConfig
{
    public static MessageHubConfiguration ConfigureLocalAnalysis(this MessageHubConfiguration config)
        => config
            .WithContentType<AnalysisContent>()
            .AddData(data => data
                .WithVirtualDataSource("ReferenceData", vs => vs
                    .WithVirtualType<AmountType>(
                        workspace => FutuReDataLoader.LoadAmountTypes(workspace)))
                .WithVirtualDataSource("LocalData", vs => vs
                    .WithVirtualType<FutuReDataCube>(
                        workspace => FutuReDataLoader.LoadLocalDataCube(workspace))))
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout.AddProfitabilityLayoutAreas());
}
