using Markdig;

namespace MeshWeaver.Layout.Markdown;

public static  class MarkdownExtensions
{
    public static MarkdownPipeline CreateMarkdownPipeline(object address) =>
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .UseYamlFrontMatter()
            .Use(new LayoutAreaMarkdownExtension())
            .Use(new ImgPathMarkdownExtension(path => ToStaticHref(path, address)))
            .Build();

    public static string ToStaticHref(string url, object address)
        => $"static/{address}/{url}";

}
