using MeshWeaver.Application;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;

namespace MeshWeaver.Portal.Web
{
    internal static class PortalHubConfiguration
    {
        internal static MessageHubConfiguration ConfigurePortalHubs(this MessageHubConfiguration configuration, UiAddress address)
            => configuration.AddBlazor(x =>
                    x.AddChartJs()
                        .AddAgGrid()
                )
                .AddData()
                .AddDocumentation()
                .ConfigureOrleansHub(address)
            ;

    }
}
