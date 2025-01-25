using Markdig;

namespace MeshWeaver.Markdown;

public static  class MarkdownExtensions
{
    public static MarkdownPipeline CreateMarkdownPipeline(object collection, object defaultAddress) =>
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .UseYamlFrontMatter()
            .Use(new ImgPathMarkdownExtension(path => ToStaticHref(path, collection)))
            .Use(new LayoutAreaMarkdownExtension(defaultAddress))
            .Build();

    public static string ToStaticHref(string path, object collection)
        => $"static/{collection}/{path}";

}
