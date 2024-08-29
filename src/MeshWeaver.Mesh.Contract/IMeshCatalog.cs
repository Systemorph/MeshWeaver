using MeshWeaver.Messaging;
using Orleans;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Contract;



public interface IMeshCatalog
{
    string GetNodeId(object address);
    Task<MeshNode> GetNodeAsync(object address);
    Task<MeshNode> GetNodeById(string id);
    Task UpdateAsync(MeshNode node);
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<ArticleEntry> GetArticle(string id);
    Task UpdateArticle(ArticleEntry article);
}

[GenerateSerializer]

public record StreamInfo(string Id, string StreamProvider, string Namespace, object Address);
[GenerateSerializer]
public record NodeStorageInfo(string NodeId, string BaseDirectory, string AssemblyLocation, object Address);
