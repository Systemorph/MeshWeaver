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
        config.TypeRegistry.WithType(typeof(KernelEventEnvelope), nameof(KernelEventEnvelope));
        config.TypeRegistry.WithType(typeof(KernelCommandEnvelope), nameof(KernelCommandEnvelope));
        config.TypeRegistry.WithType(typeof(SubscribeKernelEventsRequest), nameof(SubscribeKernelEventsRequest));
        config.TypeRegistry.WithType(typeof(UnsubscribeKernelEventsRequest), nameof(UnsubscribeKernelEventsRequest));
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
        AssemblyLocation = typeof(KernelNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddKernelHandlers()
    };
}
