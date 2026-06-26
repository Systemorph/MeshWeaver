using System.Collections.Immutable;
using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Kernel;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Markdown;

/// <summary>
/// Markdig extension that turns fenced code blocks into <see cref="ExecutableCodeBlock"/> instances,
/// enabling <c>--execute</c>/<c>--render</c> code submission and <c>layout</c> area embedding.
/// </summary>
public class ExecutableCodeBlockExtension : IMarkdownExtension
{
    /// <summary>The fenced-code-block parser that produces <see cref="ExecutableCodeBlock"/> nodes.</summary>
    public ExecutableCodeBlockParser Parser { get; } = new();

    /// <summary>The HTML renderer for executable code blocks.</summary>
    public ExecutableCodeBlockRenderer Renderer { get; } = new();

    /// <summary>
    /// Replaces Markdig's default fenced-code-block parser with <see cref="Parser"/>.
    /// </summary>
    /// <param name="pipeline">The pipeline builder being configured.</param>
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.BlockParsers.Replace<FencedCodeBlockParser>(Parser);
    }

    /// <summary>
    /// Replaces the default HTML code-block renderer with <see cref="Renderer"/> when rendering to HTML.
    /// </summary>
    /// <param name="pipeline">The built pipeline.</param>
    /// <param name="renderer">The renderer being configured.</param>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
            htmlRenderer.ObjectRenderers.Replace<CodeBlockRenderer>(Renderer);
    }

}

/// <summary>
/// Fenced-code-block parser that produces <see cref="ExecutableCodeBlock"/> instances instead of plain ones.
/// </summary>
public class ExecutableCodeBlockParser : FencedCodeBlockParser
{
    /// <summary>
    /// Creates an <see cref="ExecutableCodeBlock"/> for a fenced code fence, carrying over indentation and trivia.
    /// </summary>
    /// <param name="processor">The block processor at the current fence.</param>
    /// <returns>The created executable code block.</returns>
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

/// <summary>
/// A fenced code block that carries parsed fence arguments and may be executed against a kernel
/// (<c>--execute</c>/<c>--render</c>) or rendered as an embedded layout area (<c>layout</c> info string).
/// </summary>
/// <param name="parser">The block parser that created this block.</param>
public class ExecutableCodeBlock(BlockParser parser) : FencedCodeBlock(parser)
{
    private readonly BlockParser parser = parser;

    /// <summary>Fence argument name for silent execution (<c>--execute</c>).</summary>
    public const string Execute = "execute";

    /// <summary>Fence argument name for execute-and-stream-to-area (<c>--render</c>).</summary>
    public const string Render = "render";

    /// <summary>Fence argument name that disables execution (<c>--no-execute</c>).</summary>
    public const string NoExecute = "no-execute";

    /// <summary>Parsed fence arguments (key → optional value). Populated by <see cref="Initialize"/>.</summary>
    public IReadOnlyDictionary<string, string?> Args { get; set; } = ImmutableDictionary<string,string?>.Empty;

    /// <summary>The code submission derived from this block, or null when the block is documentation-only.</summary>
    public SubmitCodeRequest? SubmitCode { get; set; }

    /// <summary>The embedded layout-area component when the block's info string is <c>layout</c>; otherwise null.</summary>
    public LayoutAreaComponentInfo? LayoutAreaComponent { get; set; }

    /// <summary>Validation error from parsing a <c>layout</c> block, or null when parsing succeeded.</summary>
    public string? LayoutAreaError { get; set; }

    /// <summary>
    /// Parses a fence argument string (e.g. <c>--execute id --render Area</c>) into key/value pairs.
    /// Flags without a following value yield a null value.
    /// </summary>
    /// <param name="arguments">The raw argument string after the language info, or null.</param>
    /// <returns>The parsed argument key/value pairs.</returns>
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

    /// <summary>
    /// Determines the <see cref="SubmitCodeRequest"/> for this block based on its fence arguments:
    /// <c>--execute</c> or <c>--render</c> produce a submission; bare code blocks are documentation-only (null).
    /// </summary>
    /// <returns>The submission to send to the kernel, or null when the block should not execute.</returns>
    public SubmitCodeRequest? GetSubmitCodeRequest()
    {
        if (Info == "layout")
            return null;

        // --execute: silent execution (explicit opt-in).
        if (Args.TryGetValue(Execute, out var executionId))
            return new(string.Join('\n', Lines.Lines)) { Id = executionId ?? Guid.NewGuid().AsString() };
        if (SubmitCode is not null)
            return SubmitCode;
        // --render <AreaId>: execute + stream output to a named layout area.
        if (Args.TryGetValue(Render, out var renderId))
            return new(string.Join('\n', Lines.Lines)) { Id = renderId ?? Guid.NewGuid().AsString() };
        // Bare ```csharp blocks are documentation-only by default.
        // Use --execute for silent execution or --render <AreaId> to stream output.
        return null;
    }

    /// <summary>
    /// Parses a <c>layout</c> fenced block (YAML-like <c>address</c>/<c>area</c>/<c>id</c> fields, or a
    /// legacy bare address path) into a <see cref="LayoutAreaComponentInfo"/>. On failure, sets
    /// <see cref="LayoutAreaError"/> and returns null. Returns null for non-<c>layout</c> blocks.
    /// </summary>
    /// <returns>The embedded layout-area component, or null when the block is not a valid layout block.</returns>
    public LayoutAreaComponentInfo? GetLayoutAreaComponent()
    {
        if (Info != "layout")
            return null;

        var content = string.Join('\n', Lines.Lines ?? []).Trim();
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
            
            try
            {
                // Build original path from components for display purposes
                var originalPath = BuildOriginalPath(address, area, id);
                return new LayoutAreaComponentInfo(originalPath, address, area, id, parser);
            }
            catch (ArgumentException ex)
            {
                LayoutAreaError = $"Layout area validation error: {ex.Message}";
                return null;
            }
        }


        // Validate that content looks like an address for backward compatibility
        if (!content.Contains('/'))
        {
            LayoutAreaError = $"Invalid address format '{content}'. Expected format: 'addressType/addressId' or use YAML format with separate 'address' and 'area' fields.";
            return null;
        }

        try
        {
            // For backward compatibility, construct URL in expected format
            var url = $"{content}";
            return new LayoutAreaComponentInfo(url, parser);
        }
        catch (ArgumentException ex)
        {
            LayoutAreaError = $"Layout area validation error: {ex.Message}";
            return null;
        }
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

    /// <summary>
    /// Builds the original path representation from components for display purposes.
    /// This preserves the expected display format when using YAML-like syntax.
    /// </summary>
    private static string BuildOriginalPath(string address, string? area, string? id)
    {
        if (string.IsNullOrEmpty(area))
            return address;
        if (string.IsNullOrEmpty(id))
            return $"{address}/{area}";
        return $"{address}/{area}/{id}";
    }

    /// <summary>
    /// (Re)computes the derived state of the block — <see cref="Args"/>, <see cref="SubmitCode"/>,
    /// <see cref="LayoutAreaError"/>, and <see cref="LayoutAreaComponent"/> — from the raw fence content.
    /// Safe to call multiple times; the rendering and extraction paths call it before use.
    /// </summary>
    public void Initialize()
    {
        Args = ParseArguments(Arguments).ToDictionary();
        SubmitCode = GetSubmitCodeRequest();
        LayoutAreaError = null; // Reset error state
        LayoutAreaComponent = GetLayoutAreaComponent();
    }
}
