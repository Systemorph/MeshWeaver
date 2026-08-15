using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.Hosting.Snowflake.SnowflakeStorageProvider]

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Boot-pack registration for the Snowflake storage backend. Loading this DLL via
/// <c>Modules:Assemblies</c> registers the keyed <c>IStorageAdapterFactory</c> (name
/// <c>Snowflake</c>) plus the native <c>SnowflakeMeshQuery</c> (query + vector search) —
/// everything a deployment needs for <c>Graph:Storage:Type = Snowflake</c> to resolve, with no
/// compiled reference from the portal. Module installation runs BEFORE persistence selection
/// reads <c>Graph:Storage</c>, so the factory is always registered in time.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SnowflakeStorageProviderAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Hosting.Snowflake")
        {
            Name = "Snowflake storage backend",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddSnowflakeStorageFactory()),
    ];
}
