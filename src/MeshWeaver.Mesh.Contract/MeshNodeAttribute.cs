using System.Net.Mime;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{

    public abstract IMessageHub Create(IServiceProvider serviceProvider, MeshNode meshNode);
    public virtual IEnumerable<MeshNode> Nodes => [];

    protected IMessageHub CreateIf(bool condition, Func<IMessageHub> factory)
    => condition ? factory() : null;

}
