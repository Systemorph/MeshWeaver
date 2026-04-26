using System.Collections.Generic;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Static node providers that publish the test-only NodeType definitions
/// used by the validator subclasses. Pattern documented in
/// <c>Doc/Architecture/TestStateIsolation</c>:
///
/// - The NodeType definition is exposed via <see cref="IStaticNodeProvider"/>
///   instead of <c>builder.AddMeshNodes(...)</c> so it ships
///   <see cref="MeshNode.HubConfiguration"/> + <see cref="MeshNode.AssemblyLocation"/>.
///   With both set, <c>NodeTypeService.EnrichWithNodeTypeAsync</c> short-circuits
///   the dynamic-compilation lookup that would otherwise log
///   <c>"NodeType definition not found at path '&lt;type&gt;'"</c> and the per-node
///   hub spins up with the right wiring (<c>AddMeshDataSource</c>) so
///   <c>GetDataRequest</c> / <c>MeshNodeReference</c> resolves.
///
/// - Each provider is small + scoped to a single NodeType so test classes
///   register only what they need: every test owns a fresh mesh, but the
///   provider keeps the type-config knowledge in one place rather than
///   duplicating <c>HubConfiguration = c =&gt; c.AddMeshDataSource()</c> across
///   five <c>ConfigureMesh</c> overrides.
/// </summary>
internal static class NodeOperationsTypeNames
{
    public const string ContentRequired = "content-required";
    public const string Validated = "validated";
    public const string Combined = "combined";
    public const string Readable = "readable";
    public const string Updatable = "updatable";
}

/// <summary>
/// Yields the NodeType definition node + a Partition node declaring the
/// type's namespace as <c>DataSource = "static"</c>. The Partition node is
/// what tells <see cref="MeshWeaver.Hosting.Persistence.RoutingPersistenceServiceCore"/>
/// to back the namespace with a read-only
/// <see cref="MeshWeaver.Hosting.Persistence.StaticNodePartitionStore"/>
/// instead of a writable
/// <see cref="MeshWeaver.Hosting.Persistence.InMemoryPersistenceService"/> —
/// so the test-only NodeType definition stays addressable by routing without
/// being mutable. See
/// <c>Doc/Architecture/PartitionedPersistence.md</c> §"Where Partitions Come From".
/// </summary>
internal static class NodeOperationsTypeProviderHelpers
{
    /// <summary>
    /// The type-def MeshNode that lives at <c>Path == typeName</c>. Carries
    /// <see cref="MeshNode.HubConfiguration"/> + <see cref="MeshNode.AssemblyLocation"/>
    /// so the per-node hub for instances of this type wires up correctly, AND
    /// a <see cref="NodeTypeDefinition"/> Content so
    /// <c>NodeTypeService.GatherInputsAsync</c> can compile it (the generated
    /// attribute-source always emits <c>AddMeshDataSource()</c> in
    /// <c>ConfigureHub</c>, which is what makes <c>GetDataRequest</c> reach the
    /// instance hub).
    /// </summary>
    public static MeshNode TypeDefinition(
        string typeName,
        string displayName,
        System.Reflection.Assembly assembly) =>
        new(typeName)
        {
            Name = displayName,
            NodeType = "NodeType",
            AssemblyLocation = assembly.Location,
            HubConfiguration = c => c.AddMeshDataSource(),
            Content = new NodeTypeDefinition
            {
                Description = $"Test NodeType '{typeName}'."
            }
        };

    /// <summary>
    /// The companion Partition node declaring <c>Namespace = typeName</c> as
    /// <c>DataSource = "static"</c>. Tells
    /// <see cref="MeshWeaver.Hosting.Persistence.RoutingPersistenceServiceCore"/>
    /// to back the namespace with a read-only
    /// <see cref="MeshWeaver.Hosting.Persistence.StaticNodePartitionStore"/>
    /// so the type-def is reachable via <c>meshStorage.GetNodeAsync(typeName)</c>
    /// — which is what
    /// <c>NodeTypeService.GatherInputsAsync</c> does to find the
    /// <see cref="NodeTypeDefinition"/> content for compilation. Static-provider
    /// partitions are NEVER wrapped by <c>InMemoryPersistenceService</c>; they
    /// are immutable. See
    /// <c>Doc/Architecture/PartitionedPersistence.md</c> §"Where Partitions Come From".
    /// </summary>
    public static MeshNode StaticPartitionFor(string typeNamespace) =>
        new(typeNamespace, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = $"{typeNamespace} (static)",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = typeNamespace,
                DataSource = "static",
                Description = $"Test NodeType definition partition for '{typeNamespace}'"
            }
        };
}

internal sealed class ContentRequiredTypeProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return NodeOperationsTypeProviderHelpers.TypeDefinition(
            NodeOperationsTypeNames.ContentRequired,
            "Content Required",
            typeof(ContentRequiredTypeProvider).Assembly);
        yield return NodeOperationsTypeProviderHelpers.StaticPartitionFor(
            NodeOperationsTypeNames.ContentRequired);
    }
}

internal sealed class ValidatedTypeProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return NodeOperationsTypeProviderHelpers.TypeDefinition(
            NodeOperationsTypeNames.Validated,
            "Validated",
            typeof(ValidatedTypeProvider).Assembly);
        yield return NodeOperationsTypeProviderHelpers.StaticPartitionFor(
            NodeOperationsTypeNames.Validated);
    }
}

internal sealed class CombinedTypeProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return NodeOperationsTypeProviderHelpers.TypeDefinition(
            NodeOperationsTypeNames.Combined,
            "Combined",
            typeof(CombinedTypeProvider).Assembly);
        yield return NodeOperationsTypeProviderHelpers.StaticPartitionFor(
            NodeOperationsTypeNames.Combined);
    }
}

internal sealed class ReadableTypeProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return NodeOperationsTypeProviderHelpers.TypeDefinition(
            NodeOperationsTypeNames.Readable,
            "Readable",
            typeof(ReadableTypeProvider).Assembly);
        yield return NodeOperationsTypeProviderHelpers.StaticPartitionFor(
            NodeOperationsTypeNames.Readable);
    }
}

internal sealed class UpdatableTypeProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return NodeOperationsTypeProviderHelpers.TypeDefinition(
            NodeOperationsTypeNames.Updatable,
            "Updatable",
            typeof(UpdatableTypeProvider).Assembly);
        yield return NodeOperationsTypeProviderHelpers.StaticPartitionFor(
            NodeOperationsTypeNames.Updatable);
    }
}
