using MeshWeaver.Blazor;
using MeshWeaver.Documentation;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Overview;

namespace MeshWeaver.Portal
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
