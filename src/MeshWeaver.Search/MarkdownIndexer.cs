using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdown = Markdig.Markdown;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Search;

public static class MarkdownIndexer
{
    public static (MeshArticleIndex Article, string Html) ParseArticle(string path, string markdownText, object address)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(address);
        var document = Markdown.Parse(markdownText, pipeline);

        var article = GetArticle(path, document, address);
        return (article, document.ToHtml(pipeline));
    }

    private static MeshArticleIndex GetArticle(string path, MarkdownDocument document, object address)
    {

        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock != null)
        {
            var yaml = yamlBlock.Lines.ToString();
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            var ret = deserializer.Deserialize<MeshArticleIndex>(yaml);
            ret.Path = path;
        }

        return null;
    }

    private const string IndexName = "articles";
    private static void CreateIndex(SearchIndexClient searchIndexClient)
    {
        var fieldBuilder = new FieldBuilder();
        var searchFields = fieldBuilder.Build(typeof(MeshArticleIndex));

        var definition = new SearchIndex(IndexName)
        {
            Fields = searchFields
        };

        searchIndexClient.CreateIndex(definition);
        Console.WriteLine("Index created");
    }
}
