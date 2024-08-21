using MeshWeaver.Messaging;

namespace MeshWeaver.Portal
{
    internal static class PortalHubConfiguration
    {
        internal static MessageHubConfiguration ConfigurePortalHubs(this MessageHubConfiguration configuration)
            => configuration;

    }
}
