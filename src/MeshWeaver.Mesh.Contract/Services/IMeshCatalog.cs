using MeshWeaver.Messaging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Services;

public interface IMeshCatalog
{
    MeshConfiguration Configuration { get; }
    Task<MeshNode> GetNodeAsync(string addressType, string id);
    Task UpdateAsync(MeshNode node);
    Task InitializeAsync(CancellationToken cancellationToken);


}



public record StreamInfo(string AddressType, string Id, string StreamProvider, string Namespace);

public record StorageInfo(
    string Id, 
    string BaseDirectory, 
    string AssemblyLocation, 
    string AddressType);


public record StartupInfo(Address Address, string PackageName, string AssemblyLocation);
