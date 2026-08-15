using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.Hosting.Cosmos.CosmosStorageProvider]

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Boot-pack registration for the Cosmos DB storage backend. Loading this DLL via
/// <c>Modules:Assemblies</c> registers the keyed <c>IStorageAdapterFactory</c> (name
/// <c>Cosmos</c>) plus the native <c>CosmosMeshQuery</c> — everything a deployment needs for
/// <c>Graph:Storage:Type = Cosmos</c> to resolve, with no compiled reference from the portal.
/// Module installation runs BEFORE persistence selection reads <c>Graph:Storage</c>, so the
/// factory is always registered in time.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CosmosStorageProviderAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Hosting.Cosmos")
        {
            Name = "Cosmos DB storage backend",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddCosmosStorageFactory()),
    ];
}
