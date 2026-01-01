using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{
    public virtual IEnumerable<MeshNode> Nodes => [];

    /// <summary>
    /// Gets the address types to register with the mesh hub.
    /// Key is the short type name (e.g., "story"), Value is the address type.
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, Type>> AddressTypes => [];

    /// <summary>
    /// Creates a mesh node from a hub configuration using a string prefix.
    /// </summary>
    protected MeshNode CreateFromHubConfiguration(string prefix, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => new(prefix)
        {
            Name = name,
            AssemblyLocation = GetType().Assembly.Location,
            HubConfiguration = Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(hubConfiguration)
        };

    /// <summary>
    /// Creates a mesh node from a hub configuration using an Address.
    /// </summary>
    protected MeshNode CreateFromHubConfiguration(Address address, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => CreateFromHubConfiguration(address.ToString(), name, hubConfiguration);
}
