using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{

    public abstract IMessageHub Create(IMessageHub meshHub, MeshNode meshNode);
    public virtual IEnumerable<MeshNode> Nodes => [];

    public abstract bool Matches(IMessageHub meshHub, MeshNode meshNode);

}
