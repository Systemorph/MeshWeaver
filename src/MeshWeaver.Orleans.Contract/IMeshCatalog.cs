using MeshWeaver.Messaging;
using System.Collections.Immutable;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Orleans.Contract;



public interface IMeshCatalog
{
    void Configure(Func<MeshNodeInfoConfiguration, MeshNodeInfoConfiguration> config);
    string GetMeshNodeId(object address);
    Task<MeshNode> GetNodeAsync(object address);
    public Task UpdateMeshNodeAsync(MeshNode node);
}

public record MeshNodeInfoConfiguration
{
    internal ImmutableList<Func<object, string>> ModuleLoaders { get; init; }
        = [];

    public MeshNodeInfoConfiguration WithModuleMapping(Func<object, string> moduleInfoProvider)
        => this with { ModuleLoaders = ModuleLoaders.Insert(0, moduleInfoProvider) };

}
