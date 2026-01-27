using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Kernel.Hub;

public static class KernelExtensions
{
    /// <summary>
    /// Registers a kernel node that handles all kernel/* addresses.
    /// The kernel node matches any path starting with "kernel/" using score-based matching.
    /// </summary>
    public static MeshBuilder AddKernel(this MeshBuilder builder)
        => builder
            // Register Kernel types at mesh level for proper deserialization
            .ConfigureHub(AddKernelTypes)
            .AddMeshNodes(
                new MeshNode(AddressExtensions.KernelType)
                {
                    Name = "Kernel",
                    AssemblyLocation = typeof(KernelExtensions).Assembly.Location,
                    HubConfiguration = ConfigureHub,
                    Description = "Jupyter kernel for code execution"
                }
            );

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

    private static MessageHubConfiguration ConfigureHub(this MessageHubConfiguration config)
    {
        var kernelContainer = new KernelContainer(config.ParentHub!.ServiceProvider);
        return kernelContainer.ConfigureHub(config);
    }
}
