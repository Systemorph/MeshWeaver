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
    public string? LayoutAreaError { get; set; }

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

        var content = string.Join('\n', Lines.Lines).Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            LayoutAreaError = "Layout area content is empty. Please specify address and area.";
            return null;
        }

        // Try to parse as YAML-like structure first
        var yamlData = ParseYamlLike(content);
        if (yamlData != null)
        {
            var address = yamlData.GetValueOrDefault("address", "");
            var area = yamlData.GetValueOrDefault("area", Args.GetValueOrDefault(Render, ""));
            var id = yamlData.GetValueOrDefault("id", null);
            
            if (string.IsNullOrWhiteSpace(address))
            {
                LayoutAreaError = "Missing required 'address' field in layout area configuration.";
                return null;
            }
            
            if (string.IsNullOrWhiteSpace(area))
            {
                LayoutAreaError = "Missing required 'area' field in layout area configuration.";
                return null;
            }
            
            return new LayoutAreaComponentInfo(address, area, id, parser);
        }

        // Fall back to single string format for backward compatibility
        if (!Args.TryGetValue(Render, out var fallbackArea) || string.IsNullOrWhiteSpace(fallbackArea))
        {
            LayoutAreaError = $"Invalid layout area format. Expected YAML format with 'address' and 'area' fields, or specify area name with --render argument.";
            return null;
        }

        // Validate that content looks like an address for backward compatibility
        if (!content.Contains('/'))
        {
            LayoutAreaError = $"Invalid address format '{content}'. Expected format: 'addressType/addressId' or use YAML format with separate 'address' and 'area' fields.";
            return null;
        }

        return new LayoutAreaComponentInfo(content, fallbackArea, null, parser);
    }

    private static Dictionary<string, string?>? ParseYamlLike(string content)
    {
        try
        {
            var result = new Dictionary<string, string?>();
            var lines = content.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex == -1)
                    return null; // Not YAML-like format

                var key = trimmed[..colonIndex].Trim();
                var value = trimmed[(colonIndex + 1)..].Trim();
                
                // Remove quotes if present
                if (value.StartsWith('"') && value.EndsWith('"'))
                    value = value[1..^1];
                else if (value.StartsWith('\'') && value.EndsWith('\''))
                    value = value[1..^1];

                result[key] = value;
            }

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    public void Initialize()
    {
        Args = ParseArguments(Arguments).ToDictionary();
        SubmitCode = GetSubmitCodeRequest();
        LayoutAreaError = null; // Reset error state
        LayoutAreaComponent = GetLayoutAreaComponent();
    }
}
