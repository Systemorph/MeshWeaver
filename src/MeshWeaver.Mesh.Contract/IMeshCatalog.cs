using Orleans;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Contract;

public interface IMeshCatalog
{
    Task<MeshNode> GetNodeAsync(string id);
    Task UpdateAsync(MeshNode node);
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<MeshArticle> GetArticleAsync(string application, string id, bool includeContent = false);

    Task UpdateArticleAsync(MeshArticle meshArticle);

}

[GenerateSerializer]

public record StreamInfo(string Id, string StreamProvider, string Namespace, string AddressType);
[GenerateSerializer]
public record NodeStorageInfo(string Id, string BaseDirectory, string AssemblyLocation, string AddressType);


