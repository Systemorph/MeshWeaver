using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Mesh;

namespace MeshWeaver.Markdown;

public static class MarkdownIndexer
{
    public static (MeshArticle Article, string Html) ParseArticle(string path, string markdownText, string application)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(application);
        var document = Markdig.Markdown.Parse(markdownText, pipeline);

        var article = GetArticle(path, document, application);
        return (article, document.ToHtml(pipeline));
    }

    private static MeshArticle GetArticle(string path, MarkdownDocument document, string application)
    {

        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock != null)
        {
            var yaml = yamlBlock.Lines.ToString();
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            var ret = deserializer.Deserialize<MeshArticle>(yaml);

            var id = Path.GetFileNameWithoutExtension(path);
            
            return ret with
            {
                Id = id,
                Application = application,
                Path = path,
                Url = $"{application}/{id}",
                Extension = Path.GetExtension(path),
            };
        }

        return null;
    }

}
