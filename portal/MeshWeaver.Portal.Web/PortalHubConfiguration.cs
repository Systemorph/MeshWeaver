using MeshWeaver.Blazor;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Messaging;
using MeshWeaver.Overview;

namespace MeshWeaver.Portal.Web
{
    internal static class PortalHubConfiguration
    {
        internal static MessageHubConfiguration ConfigurePortalHubs(this MessageHubConfiguration configuration)
            => configuration.AddBlazor()
                .AddData()
                .AddDocumentation()
                .AddMeshWeaverOverview();

    }
}
