using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Articles;

public class ArticleService : IArticleService
{
    public ArticleService(IMeshCatalog meshCatalog)
    {
        Configuration = meshCatalog.Configuration.GetListOfLambdas().Aggregate(new ArticleConfiguration(), (l,c) => c.Invoke(l));
    }

    public ArticleConfiguration Configuration { get; }

    public ArticleCollection GetCollection(string collection)
        => Configuration.Collections.GetValueOrDefault(collection);


}
