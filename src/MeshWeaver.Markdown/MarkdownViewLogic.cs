using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Kernel;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;

/// <summary>
/// Pure helpers that back the Blazor MarkdownView / CollaborativeMarkdownView components.
/// No Blazor, no DI, no async — every method is directly unit-testable.
///
/// Two distinct concerns live here:
///
/// 1. Coercion of layout-stream values. MarkdownControl declares Markdown/Html/CodeSubmissions
///    as <c>object</c>, so when a control round-trips through the layout stream the values
///    arrive on the client as <see cref="JsonElement"/>. These coerce helpers unbox them back
///    to the typed values the view needs.
///
/// 2. Markdig pipeline orchestration. Parsing markdown, extracting executable code blocks,
///    rendering to HTML, and substituting the kernel-address placeholder. Identical between
///    the simple and collaborative views.
/// </summary>
public static class MarkdownViewLogic
{
    public static string? CoerceString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement e => e.GetRawText(),
        _ => value.ToString()
    };

    public static bool CoerceBool(object? value, bool defaultValue = false) => value switch
    {
        null => defaultValue,
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        _ => defaultValue
    };

    /// <summary>
    /// Returns a typed list of submissions from any wire form:
    /// already-typed list, JsonElement array (the layout-stream round-trip case),
    /// or null for everything else. Malformed JSON returns null rather than throwing —
    /// the caller handles the "no submissions" state gracefully.
    /// </summary>
    public static IReadOnlyList<SubmitCodeRequest>? CoerceCodeSubmissions(
        object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null: return null;
            case IReadOnlyList<SubmitCodeRequest> typed: return typed;
            case IEnumerable<SubmitCodeRequest> typed: return typed.ToList();
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                try
                {
                    return JsonSerializer.Deserialize<IReadOnlyList<SubmitCodeRequest>>(
                        array.GetRawText(), options);
                }
                catch (JsonException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// Full parse + render pipeline used by the Blazor views.
    /// Applies the annotation transform (for collaborative views) before Markdig,
    /// extracts every <see cref="ExecutableCodeBlock"/>, and returns rendered HTML
    /// with the kernel-address placeholder still embedded — call
    /// <see cref="ReplaceKernelPlaceholder"/> once the kernel address is known.
    /// </summary>
    public static MarkdownRenderResult Render(
        string markdown,
        object? collection,
        string? currentNodePath)
    {
        if (string.IsNullOrEmpty(markdown))
            return new MarkdownRenderResult(string.Empty, null);

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection, currentNodePath);
        var transformed = AnnotationMarkdownExtension.TransformAnnotations(markdown);
        var document = Markdig.Markdown.Parse(transformed, pipeline);

        var submissions = ExtractSubmissions(document);
        var html = document.ToHtml(pipeline);

        return new MarkdownRenderResult(html, submissions);
    }

    /// <summary>
    /// Parses the markdown only to recover the <see cref="SubmitCodeRequest"/> list —
    /// used when the server pre-rendered HTML (so Html is already set) but the
    /// CodeSubmissions sidecar didn't survive the wire trip.
    /// </summary>
    public static IReadOnlyList<SubmitCodeRequest>? ExtractCodeSubmissions(
        string markdown,
        object? collection,
        string? currentNodePath)
    {
        if (string.IsNullOrEmpty(markdown))
            return null;

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection, currentNodePath);
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        return ExtractSubmissions(document);
    }

    /// <summary>
    /// Extracts submissions from an already-parsed document. Use when the caller
    /// needs the <see cref="MarkdownDocument"/> for additional rendering (e.g. source-map
    /// rendering in CollaborativeMarkdownView) and we don't want to parse twice.
    /// </summary>
    public static IReadOnlyList<SubmitCodeRequest>? ExtractCodeSubmissions(MarkdownDocument document)
        => ExtractSubmissions(document);

    /// <summary>
    /// Substitutes the literal placeholder <c>__KERNEL_ADDRESS__</c> in the rendered HTML
    /// with the actual kernel address this view instance will post submissions to.
    /// </summary>
    public static string ReplaceKernelPlaceholder(string html, Address kernelAddress)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (!html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)) return html;
        return html.Replace(
            ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            kernelAddress.ToString());
    }

    /// <summary>
    /// Posts each submission to the kernel address. The mesh routing rule
    /// (<c>RouteAddressToHostedHub</c>, registered by <c>KernelNodeType.AddKernel</c>)
    /// creates the kernel hub on demand when the first message arrives.
    /// </summary>
    public static void SubmitCode(
        IMessageHub senderHub,
        Address kernelAddress,
        IReadOnlyCollection<SubmitCodeRequest> submissions)
    {
        foreach (var submission in submissions)
            senderHub.Post(submission, o => o.WithTarget(kernelAddress));
    }

    private static IReadOnlyList<SubmitCodeRequest>? ExtractSubmissions(MarkdownDocument document)
    {
        var blocks = document.Descendants<ExecutableCodeBlock>().ToList();
        if (blocks.Count == 0) return null;

        var submissions = new List<SubmitCodeRequest>(blocks.Count);
        foreach (var block in blocks)
        {
            block.Initialize();
            if (block.SubmitCode is { } s) submissions.Add(s);
        }
        return submissions.Count > 0 ? submissions : null;
    }
}

public sealed record MarkdownRenderResult(
    string Html,
    IReadOnlyList<SubmitCodeRequest>? CodeSubmissions);
