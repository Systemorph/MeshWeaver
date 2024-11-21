using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{

    public abstract IMessageHub Create(IServiceProvider serviceProvider, object address);
    public abstract MeshNode Node { get; }

    protected MeshNode GetMeshNode(object address, string location)
    {
        var basePathLength = location.LastIndexOf(Path.DirectorySeparatorChar);
        return new(address.ToString(), "Mesh Weaver Overview",
            location.Substring(0, basePathLength),
            location.Substring(basePathLength + 1))
        {
            AddressType = address.GetType().FullName
        };
    }

}
