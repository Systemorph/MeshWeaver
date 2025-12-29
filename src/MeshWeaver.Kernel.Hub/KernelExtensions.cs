using System.Reactive.Linq;
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
        => builder.AddMeshNodes(
            new MeshNode(AddressExtensions.KernelType)
            {
                Name = "Kernel",
                AssemblyLocation = typeof(KernelExtensions).Assembly.Location,
                HubConfiguration = Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(ConfigureHub),
                Description = "Jupyter kernel for code execution",
                AddressSegments = 2 // "kernel/{id}" enables dynamic child nodes
            }
        );

    private static MessageHubConfiguration ConfigureHub(this MessageHubConfiguration config)
    {
        var kernelContainer = new KernelContainer(config.ParentHub!.ServiceProvider);
        return kernelContainer.ConfigureHub(config);
    }
}
