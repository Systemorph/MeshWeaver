using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

public static  class HostingExtensions
{
    public static IMessageHub CreateHub(this IMessageHub meshHub, MeshNode node, string id)
    {
 
        if(node.HubFactory != null)
            return node.HubFactory(meshHub.ServiceProvider, id);
    
        throw new NotSupportedException($"Cannot implementation for hub with address {node.AddressType}/{id} at {node.AssemblyLocation}");

    }

}
