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

    /// <inheritdoc />
    public string Name => _namespace;
    /// <inheritdoc />
    public bool IsReadOnly => true;
    /// <inheritdoc />
    public IStorageAdapter Adapter { get; }
    /// <inheritdoc />
    public PartitionDefinition? PartitionDefinition { get; }
    /// <inheritdoc />
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Creates a read-only partition provider that serves a single namespace from
    /// embedded resources in <paramref name="assembly"/>.
    /// </summary>
    /// <param name="namespace">Partition namespace this provider claims (e.g. <c>Doc</c>, <c>Agent</c>).</param>
    /// <param name="assembly">Assembly whose manifest resources back the partition.</param>
    /// <param name="resourcePrefix">Manifest-resource name prefix that maps to the namespace root.</param>
    /// <param name="description">Optional human-readable description recorded on the <c>PartitionDefinition</c>.</param>
    /// <param name="seedNodes">Optional in-memory nodes layered over the embedded resources.</param>
    /// <param name="contexts">Optional partition contexts to opt into; defaults to Search, Create, Autocomplete and Browse.</param>
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

}
