using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public class FileSystemArticleCollectionFactory(IMessageHub hub) : IArticleCollectionFactory
{
    public const string SourceType = "FileSystem";
    public ContentCollection Create(ArticleSourceConfig config)
    {
        return new FileSystemContentCollection(config, hub);
    }
}
