using System.Collections.Immutable;
using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Kernel;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Markdown;

public class ExecutableCodeBlockExtension : IMarkdownExtension
{
    public ExecutableCodeBlockParser Parser { get; } = new();
    public ExecutableCodeBlockRenderer Renderer { get; } = new();

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.BlockParsers.Replace<FencedCodeBlockParser>(Parser);
    }
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
            htmlRenderer.ObjectRenderers.Replace<CodeBlockRenderer>(Renderer);
    }

}

public class ExecutableCodeBlockParser : FencedCodeBlockParser
{
    protected override FencedCodeBlock CreateFencedBlock(BlockProcessor processor)
    {
        var codeBlock = new ExecutableCodeBlock(this)
        {
            IndentCount = processor.Indent,
        };

        if (processor.TrackTrivia)
        {
            codeBlock.TriviaBefore = processor.UseTrivia(processor.Start - 1);
            codeBlock.NewLine = processor.Line.NewLine;
        }

        return codeBlock;
    }

}

public class ExecutableCodeBlock(BlockParser parser) : FencedCodeBlock(parser)
{
    public const string Execute = "execute";
    public const string Render = "render";
    public IReadOnlyDictionary<string, string?> Args { get; set; } = ImmutableDictionary<string,string?>.Empty;
    public SubmitCodeRequest? SubmitCode { get; set; }
    public LayoutAreaComponentInfo? LayoutAreaComponent { get; set; }

    public static IEnumerable<KeyValuePair<string, string?>> ParseArguments(string? arguments)
    {
        var linear = (arguments ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < linear.Length; i++)
        {
            var arg = linear[i];
            if (arg.StartsWith("--"))
            {
                var key = arg.Substring(2).ToLowerInvariant();
                string? value = null;
                if (i + 1 < linear.Length)
                    if (linear[i + 1].StartsWith("--"))
                    {
                        yield return new(key, null);
                        continue;
                    }
                    else
                        value = linear[++i].ToLowerInvariant();
                yield return new(key, value);
            }
        }

    }

    public SubmitCodeRequest? GetSubmitCodeRequest()
    {
        if (Info == "layout")
            return null;

        if(Args.TryGetValue(Execute, out var executionId))
            return new(string.Join('\n', Lines.Lines)) { Id = executionId ?? Guid.NewGuid().AsString() };
        if (SubmitCode is not null)
            return SubmitCode;
        if (Args.TryGetValue(Render, out var renderId))
            return new(string.Join('\n', Lines.Lines)) { Id = renderId ?? Guid.NewGuid().AsString() };
        return null;
    }

    public LayoutAreaComponentInfo? GetLayoutAreaComponent()
    {
        if (Info != "layout")
            return null;

        if (!Args.TryGetValue(Render, out var area) || string.IsNullOrWhiteSpace(area))
            area = Guid.NewGuid().AsString();

        var content = string.Join('\n', Lines.Lines).Trim();
        if (string.IsNullOrWhiteSpace(content))
            return null;

        return new LayoutAreaComponentInfo(content, parser);
    }

    public void Initialize()
    {
        Args = ParseArguments(Arguments).ToDictionary();
        SubmitCode = GetSubmitCodeRequest();
        LayoutAreaComponent = GetLayoutAreaComponent();
    }
}
