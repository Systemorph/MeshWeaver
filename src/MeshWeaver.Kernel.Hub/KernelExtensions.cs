using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Kernel.Hub;

public static class KernelExtensions
{
    public static MeshBuilder AddKernel(this MeshBuilder builder)
        => builder.ConfigureMesh(mesh => mesh
            .WithMeshNodeFactory((addressType, addressId) =>
                new(addressType, addressId, "Kernel", typeof(KernelExtensions).FullName)
                {
                    AssemblyLocation = typeof(KernelExtensions).Assembly.Location,
                    HubFactory = (serviceProvider, id) => serviceProvider.CreateKernelHub(id)
                }));

    public static IMessageHub CreateKernelHub(this IServiceProvider serviceProvider, string addressId) =>
        new KernelContainer(serviceProvider, addressId).Hub;
}

