using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeAttribute : Attribute
{
    public virtual IEnumerable<MeshNode> Nodes => [];

    /// <summary>
    /// Gets the node type configurations that map NodeType strings to their DataType and HubConfiguration.
    /// Used to determine how to serialize/deserialize Content and configure hubs for each node type.
    /// </summary>
    public virtual IEnumerable<NodeTypeConfiguration> NodeTypeConfigurations => [];

    /// <summary>
    /// Gets the address types to register with the mesh hub.
    /// Key is the short type name (e.g., "story"), Value is the address type.
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, Type>> AddressTypes => [];

    /// <summary>
    /// Gets the unified path prefix handlers to register with the mesh.
    /// Key is the prefix (e.g., "data", "area", "pricing"), Value is the handler.
    /// Built-in prefixes (data, area, content) are registered by default.
    /// Custom domains can register their own prefixes (e.g., "pricing:MS-2024").
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, IUnifiedPathHandler>> PathPrefixes => [];

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
