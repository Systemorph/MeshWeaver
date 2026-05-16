using MeshWeaver.Graph.Security;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Kernel session nodes in the graph.
/// Kernel nodes are ephemeral satellite nodes created when a kernel session starts
/// and deleted when it ends. Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class KernelNodeType
{
    public const string NodeType = "kernel";

    public static TBuilder AddKernel<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder
            // Register kernel message types at mesh level so JSON deserialization
            // works wherever a kernel-handler hub lives (Activity hub, markdown
            // view sub-hub, future hosts). The legacy
            // `RouteAddressToHostedHub("kernel", â€¦)` rule is gone â€” kernel work
            // runs inside the Activity MeshNode hub, addressed via its node path.
            .ConfigureHub(AddKernelTypes)
            .ConfigureServices(services =>
            {
                services.AddSingleton<INodeTypeAccessRule>(sp =>
                    new SatelliteAccessRule(NodeType, sp.GetService<ISecurityService>() ?? new NullSecurityService()));
                services.AddSingleton<IKernelHubConfigurator, KernelHubConfiguratorAdapter>();
                return services;
            });
        return builder;
    }

    /// <summary>
    /// Registers Kernel message types for JSON deserialization.
    /// Called at mesh level so types are known before routing.
    /// </summary>
    private static MessageHubConfiguration AddKernelTypes(MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest));
        config.TypeRegistry.WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
        return config;
    }

    /// <summary>
    /// Adapter that creates a fresh KernelContainer per hub and delegates configuration.
    /// </summary>
    private class KernelHubConfiguratorAdapter : IKernelHubConfigurator
    {
        public MessageHubConfiguration Configure(MessageHubConfiguration config)
        {
            var kernelContainer = new KernelContainer(config.ParentHub!.ServiceProvider);
            return kernelContainer.ConfigureHub(config);
        }

        public MessageHubConfiguration ConfigureSubHub(MessageHubConfiguration config)
        {
            var kernelContainer = new KernelContainer(config.ParentHub!.ServiceProvider);
            return kernelContainer.ConfigureSubHub(config);
        }
    }

    /// <summary>
    /// Creates the Kernel satellite type MeshNode definition.
    /// HubConfiguration resolves IKernelHubConfigurator from DI (registered by AddKernel()).
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Kernel Session",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddKernelHandlers()
    };
}
