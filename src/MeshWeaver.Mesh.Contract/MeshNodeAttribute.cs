using System.Net.Mime;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{

    public abstract IMessageHub Create(IServiceProvider serviceProvider, object address);
    public abstract MeshNode Node { get; }

    protected MeshNode GetMeshNode(object address, string location) => GetMeshNode(address.GetType().FullName, address.ToString(), location);

    protected MeshNode GetMeshNode(string addressType, string id, string location)
    {
        var basePathLength = location.LastIndexOf(Path.DirectorySeparatorChar);
        return new(typeof(MediaTypeNames.Application).FullName, id, "Mesh Weaver Overview",
            location.Substring(0, basePathLength),
            location.Substring(basePathLength + 1))
        {
            AddressType = addressType,
            
        };
    }

}
