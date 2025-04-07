using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Articles;


public class ArticleSourceConfig
{
    public string SourceType { get; set; } = FileSystemArticleCollectionFactory.SourceType;
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string BasePath { get; set; }
}

public interface IArticleCollectionFactory
{
    ContentCollection Create(ArticleSourceConfig config);
}

