using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Surfaces the <see cref="MeshConfiguration.Nodes"/> dictionary
/// (populated by <c>MeshBuilder.AddMeshNodes(...)</c> at config time) as an
/// <see cref="IStaticNodeProvider"/>. Without this bridge the AddMeshNodes
/// data — historically held in <c>InMemoryPersistenceService._nodes</c> —
/// would not reach the partition routing core's static-node fan-in path,
/// so test seed nodes (<c>TestData/...</c>) and built-in NodeType seeds would
/// be invisible to <see cref="AdapterPersistenceService.GetNode"/> / the
/// partition stores. Everything in MeshConfiguration is per-process state, so
/// a single transient enumeration is fine.
/// </summary>
internal sealed class MeshConfigurationStaticNodeProvider : IStaticNodeProvider
{
    private readonly MeshConfiguration _configuration;

    public MeshConfigurationStaticNodeProvider(MeshConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IEnumerable<MeshNode> GetStaticNodes() => _configuration.Nodes.Values;
}
