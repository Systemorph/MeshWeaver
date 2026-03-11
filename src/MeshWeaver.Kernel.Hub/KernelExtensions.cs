using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Kernel.Hub;

public static class KernelExtensions
{
    /// <summary>
    /// Registers kernel hub support:
    /// 1. Kernel message types for JSON deserialization
    /// 2. IKernelHubConfigurator implementation (KernelContainer)
    /// The Kernel satellite type definition is registered by AddKernelType() in Graph.
    /// </summary>
    public static MeshBuilder AddKernel(this MeshBuilder builder)
        => builder
            .ConfigureHub(AddKernelTypes)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IKernelHubConfigurator, KernelHubConfiguratorAdapter>();
                return services;
            });

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
}
