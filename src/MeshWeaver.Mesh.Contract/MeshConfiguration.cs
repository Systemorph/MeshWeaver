using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

public class MeshConfiguration(
    IReadOnlyDictionary<string, MeshNode> meshNodes,
    IReadOnlyCollection<Func<Address, MeshNode?>> factories,
    IReadOnlyCollection<MeshNamespace> namespaces)
{
    public IReadOnlyCollection<Func<Address, MeshNode?>> MeshNodeFactories { get; } = factories;

    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;

    /// <summary>
    /// Namespaces that describe available address types for autocomplete.
    /// These provide metadata (name, description, icon) and autocomplete routing for dynamic node types.
    /// </summary>
    public IReadOnlyCollection<MeshNamespace> Namespaces { get; } = namespaces;
}
