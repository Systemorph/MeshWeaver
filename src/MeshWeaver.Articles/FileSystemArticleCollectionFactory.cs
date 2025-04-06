using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public class FileSystemArticleCollectionFactory(IMessageHub hub) : IArticleCollectionFactory
{
    public const string SourceType = "FileSystem";
    public ArticleCollection Create(ArticleSourceConfig config)
    {
        return new FileSystemArticleCollection(config, hub);
    }
}
