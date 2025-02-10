using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Kernel.Hub;

public static class KernelExtensions
{
    public static MeshBuilder AddKernel(this MeshBuilder builder)
        => builder
            .ConfigureMesh(mesh => mesh
            .AddMeshNodeFactory(address =>
                address.Type == KernelAddress.TypeName
                    ? new(address.Type, address.Id, address.ToString())
                    {
                        AssemblyLocation = typeof(KernelExtensions).Assembly.Location,
                        HubConfiguration = ConfigureHub
                    }
                    : null
            )
        );

    private static MessageHubConfiguration ConfigureHub(this MessageHubConfiguration config)
    {
        var kernelContainer = new KernelContainer();
        return kernelContainer.ConfigureHub(config);
    }
}

