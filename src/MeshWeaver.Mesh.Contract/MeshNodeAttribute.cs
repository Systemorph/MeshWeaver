using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{
    public virtual IEnumerable<MeshNode> Nodes => [];

    /// <summary>
    /// Gets the mesh node factories for dynamic node creation.
    /// Each factory receives an Address and can return a MeshNode or null if it doesn't handle that address.
    /// </summary>
    public virtual IEnumerable<Func<Address, MeshNode?>> NodeFactories => [];

    /// <summary>
    /// Gets the address types to register with the mesh hub.
    /// Key is the short type name (e.g., "story"), Value is the address type.
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, Type>> AddressTypes => [];

    protected MeshNode CreateFromHubConfiguration(Address address, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => new(address.Type, address.Id, name)
        {
            AssemblyLocation = GetType().Assembly.Location,
            HubConfiguration = hubConfiguration
        };


}
