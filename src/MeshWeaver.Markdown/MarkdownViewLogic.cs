using System.Reactive.Linq;
using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Submits each code block to the target hub <em>in order</em>, waiting for
    /// the previous submission's <see cref="SubmitCodeResponse"/> before posting
    /// the next. Required for blocks that share REPL state: e.g. block #1 sets
    /// <c>var counter = 41;</c> and block #2 references <c>counter</c>; they
    /// must reach the kernel sequentially so block #2's
    /// <c>scriptState.ContinueWithAsync</c> sees block #1's variable.
    /// <para>
    /// The naive shape — <c>foreach Post</c> without waiting — relies on the
    /// kernel's <c>executionLock</c> SemaphoreSlim to serialise execution and
    /// hopes that the SemaphoreSlim acquires in arrival order. SemaphoreSlim
    /// is FIFO under contention, but each <c>Hub.Observe</c> + <c>Subscribe</c>
    /// pair runs on the calling hub's action block; the inner WaitAsync
    /// continuations resume on the TaskPool, so the order in which the script
    /// pipeline acquires the lock can interleave on a busy CI thread pool —
    /// surfaced as block #2 reaching <c>CSharpScript.RunAsync</c> before
    /// block #1 has stored <c>scriptState</c>, then failing with
    /// <c>error CS0103: The name 'counter' does not exist in the current
    /// context</c>.
    /// </para>
    /// <para>
    /// Caller is responsible for ensuring the target hub (typically a per-view
    /// Activity hub) exists — see <see cref="CreateActivityAndSubmit"/> for the
    /// production path that materialises the Activity first. Tests use this
    /// overload directly when the Activity is pre-created in fixture setup.
    /// </para>
    /// </summary>
    public static void SubmitCode(
        IMessageHub senderHub,
        Address target,
        IReadOnlyCollection<SubmitCodeRequest> submissions)
    {
        if (submissions.Count == 0) return;

        SubmitNext(senderHub, target, submissions.ToList(), index: 0);
    }

    private static void SubmitNext(
        IMessageHub senderHub,
        Address target,
        IReadOnlyList<SubmitCodeRequest> submissions,
        int index)
    {
        if (index >= submissions.Count) return;

        // Observe the response for this submission, then chain the next post off
        // its emission. The reactive chain stays cold; Subscribe is the side
        // effect that posts. Errors on a previous submission do NOT block
        // subsequent ones — the kernel is forgiving (compile errors render
        // inline) and skipping later blocks would silently drop user code.
        senderHub.Observe<SubmitCodeResponse>(
                submissions[index],
                o => o.WithTarget(target))
            .Take(1)
            .Subscribe(
                _ => SubmitNext(senderHub, target, submissions, index + 1),
                _ => SubmitNext(senderHub, target, submissions, index + 1));
    }

    /// <summary>
    /// Materialises a per-view Activity MeshNode (whose hub hosts the kernel
    /// handlers via <c>ActivityNodeType.HubConfiguration</c>) and posts each
    /// submission to the activity address. Replaces the legacy "post to
    /// `kernel/*` and let the mesh routing rule create a kernel hub" path.
    ///
    /// <para>Activity creation is fire-and-on-success: <c>meshService.CreateNode</c>
    /// returns an <see cref="IObservable{T}"/>; we subscribe and post the
    /// submissions when the create completes. If the create errors, the
    /// submissions are NOT posted — the error surfaces via the standard
    /// observable path.</para>
    /// </summary>
    public static void CreateActivityAndSubmit(
        IMessageHub senderHub,
        IMeshService meshService,
        Address activityAddress,
        string? ownerPath,
        string kernelId,
        IReadOnlyCollection<SubmitCodeRequest> submissions)
    {
        var activityNamespace = string.IsNullOrEmpty(ownerPath)
            ? "_Activity"
            : $"{ownerPath}/_Activity";
        var activityPath = $"{activityNamespace}/markdown-{kernelId}";
        var activityNode = new MeshNode($"markdown-{kernelId}", activityNamespace)
        {
            Name = $"Markdown view {kernelId[..Math.Min(8, kernelId.Length)]}",
            NodeType = "Activity",
            MainNode = ownerPath ?? string.Empty,
            State = MeshNodeState.Active,
            Content = new ActivityLog("MarkdownExecution")
            {
                Id = $"markdown-{kernelId}",
                HubPath = ownerPath ?? string.Empty,
                Status = ActivityStatus.Running
            }
        };

        var logger = senderHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MarkdownExecution");

        // Create the per-view Activity node, then WAIT until it is actually ROUTABLE before posting the
        // submissions. CreateNode completing means the node is PERSISTED — NOT that its per-node Activity hub
        // is registered with the router yet. Posting SubmitCodeRequest in that gap routes to a non-existent
        // address ("No node found at …/_Activity/markdown-…"). The node's own stream is served BY that hub,
        // so its first emission proves the hub is live and routable. The create/routing error is SURFACED,
        // never swallowed (a swallowed kernel-unavailable was the old bug; see RoutingServiceBase NotFound).
        meshService.CreateNode(activityNode)
            .SelectMany(_ => senderHub.GetMeshNodeStream(activityPath)
                .Where(node => node is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(15)))
            .Subscribe(
                _ => SubmitCode(senderHub, activityAddress, submissions),
                ex => logger?.LogWarning(ex,
                    "Markdown kernel activity {Path} did not become routable; code submissions not posted",
                    activityPath));
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
