# MeshWeaver.Articles

## Overview
MeshWeaver.Articles is a specialized component of the MeshWeaver ecosystem that provides functionality for article content management and rendering. This library enables applications to organize, load, and display Markdown-based articles from file system or other storage providers.

## Features
- Markdown-based article content management
- Article collections for organizing content
- Path-based article resolution
- Built-in navigation capabilities
- Catalog functionality for browsing articles
- Resolve `IArticleService` for article operations
- Integration with MeshWeaver UI components
- Extensible storage providers for article content
- Article metadata and properties support

## Configuration

### In appsettings.json
```json
{
  "ArticleCollections": [
    {
      "Name": "Documentation",
      "DisplayName": "Documentation",
      "DefaultAddress": "app/Documentation",
      "BasePath": "../../modules/Documentation/MeshWeaver.Documentation/Markdown"
    },
    {
      "Name": "Northwind",
      "DisplayName": "Northwind",
      "DefaultAddress": "app/Northwind",
      "BasePath": "../../modules/Northwind/MeshWeaver.Northwind.ViewModel/Markdown"
    }
  ]
}
```

### Configure in Services
```csharp
// Register Article services
services.AddArticles(options => {
    options.AddFileSystemArticles(
        "Documentation",
        "Documentation",
        "app/Documentation",
        Path.Combine(GetAssemblyLocation(), "Markdown"));
    
    options.AddFileSystemArticles(
        "Samples",
        "Sample Articles",
        "app/Samples",
        Path.Combine(GetAssemblyLocation(), "Samples"));
});

// Configure with ArticleCollections from configuration
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

services.AddArticles(config => {
    configuration.GetSection("ArticleCollections")
        .Get<ArticleCollection[]>()
        ?.ToList()
        .ForEach(c => {
            config.AddFileSystemArticles(
                c.Name,
                c.DisplayName,
                c.DefaultAddress,
                c.BasePath);
        });
});
```

## Usage Examples

### Loading Articles
```csharp
// Resolve IArticleService
public class ArticleViewer
{
    private readonly IArticleService _articleService;
    
    public ArticleViewer(IArticleService articleService)
    {
        _articleService = articleService;
    }
    
    // Load a specific article
    public async Task<ArticleContent> ViewArticleAsync(string path)
    {
        var article = await _articleService.GetArticleAsync(path);
        if (article == null)
            throw new ArticleNotFoundException(path);
            
        return article;
    }
    
    // Get article catalog
    public async Task<List<ArticleInfo>> GetCatalogAsync(string collection)
    {
        var catalog = await _articleService.GetCatalogAsync(collection);
        return catalog.Articles.ToList();
    }
}
```

### From Tests
```csharp
[Fact]
public async Task BasicArticle()
{
    // Get article by path
    var article = await ArticleService.GetArticleAsync($"{Test}/article");
    article.Should().NotBeNull();
    article.Content.Should().NotBeNull();
    article.Content.Should().Contain("Simple Test Article");
    article.Properties.Should().ContainKey("title");
    article.Properties["title"].Should().Be("Test Article");
}

[Fact]
public async Task Catalog()
{
    // Get catalog for a collection
    var catalog = await ArticleService.GetCatalogAsync(Test);
    catalog.Should().NotBeNull();
    catalog.Articles.Should().HaveCount(1);
    
    // Catalog contains article info
    var article = catalog.Articles.First();
    article.Title.Should().Be("Test Article");
    article.Path.Should().Be($"{Test}/article");
    
    // Get the article from catalog
    var articleContent = await ArticleService.GetArticleAsync(article.Path);
    articleContent.Should().NotBeNull();
    articleContent.Content.Should().Contain("Simple Test Article");
}
```

## Key Concepts
- **Article Collections**: Logical groupings of articles with metadata
- **Article Path**: Hierarchical identifiers for articles
- **Article Catalog**: Directory of available articles in a collection
- **Article Content**: The rendered content and metadata of an article
- **Storage Providers**: Backend systems that provide article content (file system, database, etc.)

## Integration with MeshWeaver
- Works with MeshWeaver.Layout for article rendering
- Integrates with MeshWeaver navigation components
- Supports MeshWeaver.Markdown for content processing

## Related Projects
- [MeshWeaver.Markdown](../MeshWeaver.Markdown/README.md) - Markdown processing
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - UI layout for articles
- [MeshWeaver.Documentation.Test](../../test/MeshWeaver.Documentation.Test/README.md) - Testing utilities

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
