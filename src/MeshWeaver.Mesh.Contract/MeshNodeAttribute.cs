using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{

    public abstract IMessageHub Create(IServiceProvider serviceProvider, object address);
    public abstract MeshNode Node { get; }
}
