using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Attribute to define mesh nodes at the assembly level.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeProviderAttribute : Attribute
{
    /// <summary>
    /// Gets the mesh nodes defined by this attribute.
    /// </summary>
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
            HubConfiguration = hubConfiguration
        };

    /// <summary>
    /// Creates a mesh node from a hub configuration using an Address.
    /// </summary>
    protected MeshNode CreateFromHubConfiguration(Address address, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => CreateFromHubConfiguration(address.ToString(), name, hubConfiguration);
}
