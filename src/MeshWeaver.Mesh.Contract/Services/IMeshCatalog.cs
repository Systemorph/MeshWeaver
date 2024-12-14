[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Services;

public interface IMeshCatalog
{
    Task<MeshNode> GetNodeAsync(string addressType, string id);
    Task UpdateAsync(MeshNode node);
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<MeshArticle> GetArticleAsync(string addressType, string nodeId, string id, bool includeContent = false);

    Task UpdateArticleAsync(MeshArticle meshArticle);

}



public record StreamInfo(string Id, string StreamProvider, string Namespace, string AddressType);

public record NodeStorageInfo(string Id, string BaseDirectory, string AssemblyLocation, string AddressType);


