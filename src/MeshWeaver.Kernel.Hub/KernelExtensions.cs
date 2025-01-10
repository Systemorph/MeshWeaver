using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

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
                        HubFactory = (serviceProvider, _, id) => serviceProvider.CreateKernelHub(id)
                    }
                    : null
            )
        );

    public static IMessageHub CreateKernelHub(this IServiceProvider serviceProvider, string addressId) =>
        new KernelContainer(serviceProvider, addressId).Hub;
}

