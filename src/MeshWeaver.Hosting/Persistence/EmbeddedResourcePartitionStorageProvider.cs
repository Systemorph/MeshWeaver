using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// First-match-wins partition rule that pins a single namespace to
/// an embedded-resource backed read-only adapter. Registered via
/// <c>MeshBuilder.AddEmbeddedResourcePartition(...)</c>.
/// </summary>
public sealed class EmbeddedResourcePartitionStorageProvider : IPartitionStorageProvider
{
    private readonly string _namespace;

    public string Name => _namespace;
    public IStorageAdapter Adapter { get; }
    public PartitionDefinition? PartitionDefinition { get; }
    public ImmutableHashSet<string> Contexts { get; }

    public EmbeddedResourcePartitionStorageProvider(
        string @namespace,
        Assembly assembly,
        string resourcePrefix,
        string? description = null,
        IEnumerable<MeshNode>? seedNodes = null,
        IEnumerable<string>? contexts = null)
    {
        _namespace = @namespace;
        Adapter = new EmbeddedResourceStorageAdapter(assembly, resourcePrefix, seedNodes,
            partitionNamespace: @namespace);
        PartitionDefinition = new PartitionDefinition
        {
            Namespace = @namespace,
            DataSource = "EmbeddedResource",
            Description = description,
            Versioned = false
        };
        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);
    }

    public bool Matches(string firstSegment)
        => string.Equals(firstSegment, _namespace, StringComparison.OrdinalIgnoreCase);
}
