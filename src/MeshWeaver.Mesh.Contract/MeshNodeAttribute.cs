using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{

    public virtual IEnumerable<MeshNode> Nodes => [];

}
