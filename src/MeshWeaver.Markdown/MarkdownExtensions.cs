using Markdig;

namespace MeshWeaver.Markdown;

public static  class MarkdownExtensions
{
    public static MarkdownPipeline CreateMarkdownPipeline(
        object collection
        ) =>
        new MarkdownPipelineBuilder()
            .UseMathematics()
            .UseAdvancedExtensions()
            .UseGenericAttributes()
            .UseEmojiAndSmiley()
            .UseYamlFrontMatter()
            .Use(new ImgPathMarkdownExtension(path => ToStaticHref(path, collection)))
            .Use(new LayoutAreaMarkdownExtension())
            .Use(new ExecutableCodeBlockExtension())
            .Build();

    public static string ToStaticHref(string path, object collection)
        => $"static/{collection}/{path}";

}
