using MeshWeaver.Messaging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Contract;



public interface IMeshCatalog
{
    string GetMeshNodeId(object address);
    Task<MeshNode> GetNodeAsync(object address);
    Task UpdateMeshNodeAsync(MeshNode node);
    Task InitializeAsync(CancellationToken cancellationToken);
}

public record StreamInfo(string Id, string StreamProvider, string Namespace, object Address);
public record NodeStorageInfo(string NodeId, string BaseDirectory, string AssemblyLocation, object Address);
