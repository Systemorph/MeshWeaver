using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

public static  class HostingExtensions
{
    public static async Task<IMessageHub> CreateHub(this IServiceProvider serviceProvider, string addressType, string id)
    {
        var meshCatalog = serviceProvider.GetRequiredService<IMeshCatalog>();
        var node = await meshCatalog.GetNodeAsync(addressType, id);
        if (node == null)
            return null;

        var assembly = Assembly.LoadFrom(Path.Combine(node.BasePath, node.AssemblyLocation));
        if (assembly == null)
            throw new InvalidOperationException($"Assembly {node.AssemblyLocation} not found in {node.BasePath}");

        var hub = assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Create(serviceProvider, node)).FirstOrDefault(x => x != null);

        if(hub == null)
            throw new NotSupportedException($"Cannot implementation for hub with address {addressType}/{id} at {node.AssemblyLocation}");
        return hub;

    }
}
