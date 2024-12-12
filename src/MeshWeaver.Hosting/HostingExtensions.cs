using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

public static  class HostingExtensions
{
    public static async Task<IMessageHub> CreateHub(this IMessageHub meshHub, string addressType, string id)
    {
        var meshCatalog = meshHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var node = await meshCatalog.GetNodeAsync(addressType, id);
        if (node == null)
            return null;

        if(node.HubFactory != null)
            return node.HubFactory(meshHub.ServiceProvider, id);

        var assembly = Assembly.LoadFrom(node.AssemblyLocation);
        if (assembly == null)
            throw new InvalidOperationException($"Assembly {node.AssemblyLocation} not found in {node.PackageName}");

        var hub = assembly.GetCustomAttributes<MeshNodeAttribute>()
            .Where(a => a.Matches(meshHub, node))
            .Select(a => a.Create(meshHub, node))
            .FirstOrDefault(x => x != null);

        if(hub == null)
            throw new NotSupportedException($"Cannot implementation for hub with address {addressType}/{id} at {node.AssemblyLocation}");
        return hub;

    }

}
