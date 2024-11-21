using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Mesh.Contract;

namespace MeshWeaver.Markdown;

public static class MarkdownIndexer
{
    public static (MeshArticle Article, string Html) ParseArticle(string path, string markdownText, object address)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(address);
        var document = Markdig.Markdown.Parse(markdownText, pipeline);

        var article = GetArticle(path, document, address);
        return (article, document.ToHtml(pipeline));
    }

    private static MeshArticle GetArticle(string path, MarkdownDocument document, object address)
    {

        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock != null)
        {
            var yaml = yamlBlock.Lines.ToString();
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            var ret = deserializer.Deserialize<MeshArticle>(yaml);
            ret = ret with { Path = path };
        }

        return null;
    }

}
