using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

public static class KernelExtensions
{
    public static MeshBuilder AddKernel(this MeshBuilder builder)
        => builder
            .ConfigureMesh(mesh => mesh
            .AddMeshNodeFactory((addressType, addressId) =>
                addressType == KernelAddress.TypeName
                    ? new(addressType, addressId, "Kernel", typeof(KernelExtensions).FullName)
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

