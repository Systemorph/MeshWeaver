using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public class ArticleService : IArticleService
{
    public ArticleService(IMeshCatalog meshCatalog, IMessageHub hub)
    {
        Configuration = meshCatalog.Configuration.GetListOfLambdas().Aggregate(new ArticleConfiguration(hub), (l,c) => c.Invoke(l));
    }

    public ArticleConfiguration Configuration { get; }

    public ArticleCollection GetCollection(string collection)
        => Configuration.Collections.GetValueOrDefault(collection);


}
