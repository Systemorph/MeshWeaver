using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Kernel;

namespace MeshWeaver.Markdown;

public class ExecutableCodeBlockExtension : IMarkdownExtension
{
    public ExecutableCodeBlockParser Parser { get; } = new();
    public ExecutableCodeBlockRenderer Renderer { get; } = new();

    public void Setup(MarkdownPipelineBuilder pipeline){}
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

public class ExecutableCodeBlock : FencedCodeBlock
{
    public const string Execute = "execute";
    public const string Render = "render";
    public IReadOnlyDictionary<string, string> Args => args.Value;
    private readonly Lazy<Dictionary<string, string>> args;
    public SubmitCodeRequest SubmitCode { get; private set; }


    public ExecutableCodeBlock(BlockParser parser) : base(parser)
    {
        args =   new(() => ParseArguments(this.Arguments).ToDictionary());
    }
    private static IEnumerable<KeyValuePair<string, string>> ParseArguments(string arguments)
    {
        var linear = (arguments ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < linear.Length; i++)
        {
            var arg = linear[i];
            if (arg.StartsWith("--"))
            {
                var key = arg.Substring(2).ToLowerInvariant();
                string value = null;
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

    public SubmitCodeRequest GetSubmitCodeRequest(string defaultId)
    {
        if(Args.TryGetValue(Execute, out var executionId))
            return SubmitCode = new(string.Join('\n', Lines)) { Id = executionId ?? defaultId };
        if (SubmitCode is not null)
            return SubmitCode;
        if (Args.TryGetValue(Render, out var renderId))
            return SubmitCode = new(string.Join('\n', Lines)) { Id = renderId ?? defaultId };
        return null;
    }

}
