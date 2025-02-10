using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{
    public virtual IEnumerable<MeshNode> Nodes => [];

    protected MeshNode CreateFromHubConfiguration(Address address, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => new(address.Type, address.Id, name)
        {
            AssemblyLocation = GetType().Assembly.Location, 
            HubConfiguration = hubConfiguration
        };
}
