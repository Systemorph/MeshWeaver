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
    /// <summary>
    /// Unboxes a layout-stream value to a string, handling the <see cref="JsonElement"/> round-trip
    /// (string elements decode to their value; null elements to null; other kinds to raw JSON text).
    /// </summary>
    /// <param name="value">The boxed value from the layout stream.</param>
    /// <returns>The coerced string, or null.</returns>
    public static string? CoerceString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement e => e.GetRawText(),
        _ => value.ToString()
    };

    /// <summary>
    /// Unboxes a layout-stream value to a bool, handling the <see cref="JsonElement"/> round-trip
    /// (true/false elements) and falling back to <paramref name="defaultValue"/> for anything else.
    /// </summary>
    /// <param name="value">The boxed value from the layout stream.</param>
    /// <param name="defaultValue">The value returned when <paramref name="value"/> is null or not a recognised bool.</param>
    /// <returns>The coerced boolean.</returns>
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
    /// Resolves the executable-code-block result-area placeholders for RENDER, gating the LIVE
    /// (subscribing) embed on the per-view Activity being created + routable. This is the single
    /// decision the Blazor views call at render time; it has three states:
    /// <list type="bullet">
    ///   <item><b>No owner</b> → <see cref="DisableKernelPlaceholder"/>: a static notice, never a
    ///   subscription (there is no per-node hub to host the kernel).</item>
    ///   <item><b>Owned but <paramref name="kernelReady"/> = false</b> → <see cref="PendingKernelPlaceholder"/>:
    ///   a static, NON-subscribing "starting" notice.</item>
    ///   <item><b>Owned and <paramref name="kernelReady"/> = true</b> → <see cref="ReplaceKernelPlaceholder"/>:
    ///   the live <c>data-address</c> the Blazor renderer turns into a LayoutAreaControl that
    ///   subscribes to the kernel area and renders results.</item>
    /// </list>
    ///
    /// <para>🚨 <b>Why the gate.</b> Each executable block renders a
    /// <c>&lt;div class='layout-area' data-address='__KERNEL_ADDRESS__' …&gt;</c>. Embedding the LIVE
    /// address mounts one <c>LayoutAreaView</c> per block, each subscribing — via
    /// <c>Workspace.GetRemoteStream&lt;JsonElement,LayoutAreaReference&gt;</c>, which BYPASSES the
    /// <c>MeshNodeStreamCache</c> storm-breaker — to <c>{owner}/_Activity/markdown-{id}</c>. If that
    /// embed happens at first render (as it used to), every subscriber races
    /// <see cref="CreateActivityAndSubmit"/> and hits the activity BEFORE it exists → a
    /// <c>[ROUTE] NotFound</c> burst (measured ~59× on <c>Doc/DataMesh/InteractiveMarkdown</c>: one
    /// subscriber per executable block, re-opened across the prerender→interactive transition). So the
    /// live area is embedded ONLY once the activity is routable — the view flips
    /// <paramref name="kernelReady"/> from <see cref="CreateActivityAndSubmit"/>'s <c>onReady</c>
    /// callback and re-renders. Until then the user SEES a non-subscribing placeholder.</para>
    /// </summary>
    public static string RenderKernelResultAreas(
        string? html, string? ownerPath, bool kernelReady, Address kernelAddress)
    {
        if (string.IsNullOrEmpty(html))
            return html ?? string.Empty;

        if (string.IsNullOrEmpty(ownerPath))
            return DisableKernelPlaceholder(html);

        return kernelReady
            ? ReplaceKernelPlaceholder(html, kernelAddress)
            : PendingKernelPlaceholder(html);
    }

    /// <summary>
    /// Neutralises the executable-code-block result areas when the view has NO owning node, so
    /// interactive code does NOT storm the router. Each executable block renders a
    /// <c>&lt;div class='layout-area' data-address='__KERNEL_ADDRESS__' …&gt;</c> that the Blazor
    /// renderer turns into a live LayoutAreaControl subscribing to the kernel address. Without an
    /// owner the only address we could embed is a bare <c>_Activity/markdown-{id}</c> — an
    /// ownerless path that NotFound-storms (the same defect <see cref="ActivityNodeGuard"/> blocks
    /// at create time). Instead we replace each kernel area div with a static, non-subscribing
    /// notice so the user SEES that execution is unavailable here, and the renderer never opens a
    /// subscription to a phantom address (an empty <c>data-address</c> is skipped by
    /// <c>MarkdownHtmlRenderer.RenderLayoutArea</c>; the regex below removes the whole div).
    /// </summary>
    public static string DisableKernelPlaceholder(string html)
    {
        const string notice =
            "<div class=\"markdown-kernel-disabled\" style=\"border:1px solid var(--neutral-stroke-rest,#d0d0d0);" +
            "background:var(--neutral-layer-2,#f5f5f5);color:var(--neutral-foreground-hint,#666);" +
            "padding:8px 12px;border-radius:4px;margin:8px 0;font-size:13px;\">" +
            "Interactive code execution is unavailable here — this view has no owning node to host the kernel." +
            "</div>";
        return ReplaceKernelAreaDivs(html, notice);
    }

    /// <summary>
    /// Replaces the executable-code-block result areas with a static, NON-subscribing "starting"
    /// notice while the per-view Activity is being created + brought online. This is the gap the
    /// subscribe-before-create storm used to live in: until <see cref="CreateActivityAndSubmit"/>
    /// reports the activity routable (its <c>onReady</c> callback flips the view's <c>kernelReady</c>
    /// flag), the kernel area carries NO <c>data-address</c>, so the Blazor renderer mounts no
    /// <c>LayoutAreaView</c> and opens no subscription to the not-yet-created
    /// <c>{owner}/_Activity/markdown-{id}</c>. The notice keeps the absent case VISIBLE rather than
    /// blank, and once the activity is routable the view re-renders with the live area embedded.
    /// </summary>
    public static string PendingKernelPlaceholder(string html)
    {
        const string notice =
            "<div class=\"markdown-kernel-pending\" style=\"border:1px solid var(--neutral-stroke-rest,#d0d0d0);" +
            "background:var(--neutral-layer-2,#f5f5f5);color:var(--neutral-foreground-hint,#666);" +
            "padding:8px 12px;border-radius:4px;margin:8px 0;font-size:13px;\">" +
            "Starting interactive kernel…" +
            "</div>";
        return ReplaceKernelAreaDivs(html, notice);
    }

    /// <summary>
    /// Replaces every kernel result-area div (identified by the placeholder address) with
    /// <paramref name="replacement"/>. The div is emitted single-line by
    /// <c>LayoutAreaMarkdownRenderer.GetLayoutAreaDiv</c>, so a targeted, non-greedy match is exact
    /// and never touches other layout-area divs. No-ops when the placeholder is absent.
    /// </summary>
    private static string ReplaceKernelAreaDivs(string html, string replacement)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (!html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)) return html;

        var pattern = "<div class='layout-area' data-address='"
            + System.Text.RegularExpressions.Regex.Escape(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)
            + "'[^>]*></div>";
        return System.Text.RegularExpressions.Regex.Replace(html, pattern, replacement);
    }

    /// <summary>
    /// Splits rendered HTML (with kernel-address placeholders still embedded) into an ORDERED list of
    /// segments: each is either a plain HTML chunk (<c>SubmissionId == null</c>) or a kernel result-area
    /// marker (<c>Html == null</c>, <c>SubmissionId</c> = the executable block's submission id, i.e. the
    /// layout-area name the kernel writes its result into). A view pack that cannot host a nested view
    /// inside an HTML blob (the native MAUI pack — a <c>WebView</c> can't contain native children) uses
    /// this to render HTML chunks and live kernel areas side by side. Pure + unit-testable.
    /// <para>No placeholder present ⇒ a single <c>(html, null)</c> segment; empty input ⇒ empty list.
    /// The Blazor pack does NOT use this — it embeds the live area inline via the <c>RenderLayoutArea</c>
    /// render-tree path; the split is the framework-shared equivalent for non-Blazor view packs.</para>
    /// </summary>
    public static IReadOnlyList<(string? Html, string? SubmissionId)> SplitKernelResultAreas(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return Array.Empty<(string?, string?)>();
        if (!html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder))
            return new (string?, string?)[] { (html, null) };

        // The div is emitted single-line by LayoutAreaMarkdownRenderer.GetLayoutAreaDiv as
        // <div class='layout-area' data-address='__KERNEL_ADDRESS__' data-area='{submissionId}' …></div>;
        // capture data-area (the submission id = the kernel's result-area name).
        var pattern = "<div class='layout-area' data-address='"
            + System.Text.RegularExpressions.Regex.Escape(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)
            + "' data-area='([^']*)'[^>]*></div>";

        var segments = new List<(string? Html, string? SubmissionId)>();
        var lastIndex = 0;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(html, pattern))
        {
            if (m.Index > lastIndex)
                segments.Add((html[lastIndex..m.Index], null));
            segments.Add((null, m.Groups[1].Value));
            lastIndex = m.Index + m.Length;
        }
        if (lastIndex < html.Length)
            segments.Add((html[lastIndex..], null));
        return segments;
    }

    /// <summary>A live layout-area embed parsed out of the rendered markdown HTML — the native counterpart
    /// of Blazor's inline area injection. Carries whichever attributes the <c>@@</c> renderer emitted:
    /// the pre-parsed <see cref="Address"/>/<see cref="Area"/>/<see cref="AreaId"/> (e.g.
    /// <c>@@/Acme/area/Search</c>) and/or the unresolved <see cref="RawPath"/> (e.g. <c>@@Cession/MotorXL</c>,
    /// which the host resolves against the authoring node at render time).</summary>
    public readonly record struct EmbeddedAreaRef(string? RawPath, string? Address, string? Area, string? AreaId);

    /// <summary>
    /// Splits the rendered markdown HTML into ordered (html | <see cref="EmbeddedAreaRef"/>) segments,
    /// pulling out every <c>@@</c> layout-area embed (the <c>&lt;div class='layout-area' …&gt;&lt;/div&gt;</c>
    /// the <c>@@</c> operator emits via <see cref="MeshWeaver.Markdown.LayoutAreaMarkdownRenderer"/>). The
    /// KERNEL result placeholder (<see cref="ExecutableCodeBlockRenderer.KernelAddressPlaceholder"/>) is the
    /// concern of <see cref="SplitKernelResultAreas"/> and is left in the html runs here. A native view pack
    /// (MAUI) can't hydrate the placeholder inside a WebView, so it renders the html runs as WebView chunks
    /// and each embed as a real native area view between them. Pure + unit-testable. No embed ⇒ a single
    /// <c>(html, null)</c> segment; empty input ⇒ empty list.
    /// </summary>
    public static IReadOnlyList<(string? Html, EmbeddedAreaRef? Embed)> SplitLayoutAreaRefs(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return Array.Empty<(string?, EmbeddedAreaRef?)>();

        var divRx = new System.Text.RegularExpressions.Regex(
            "<div class='" + LayoutAreaMarkdownRenderer.LayoutArea + "'(?<attrs>[^>]*)></div>");
        var matches = divRx.Matches(html);
        if (matches.Count == 0)
            return new (string?, EmbeddedAreaRef?)[] { (html, null) };

        static string? Attr(string attrs, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(attrs, "data-" + name + "='([^']*)'");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }

        var segments = new List<(string? Html, EmbeddedAreaRef?)>();
        var last = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var attrs = m.Groups["attrs"].Value;
            var address = Attr(attrs, LayoutAreaMarkdownRenderer.Address);
            // Kernel result placeholder → not an @@ embed; leave it in the html run (SplitKernelResultAreas owns it).
            if (address == ExecutableCodeBlockRenderer.KernelAddressPlaceholder)
                continue;
            if (m.Index > last)
                segments.Add((html[last..m.Index], null));
            segments.Add((null, new EmbeddedAreaRef(
                Attr(attrs, LayoutAreaMarkdownRenderer.RawPath),
                address,
                Attr(attrs, LayoutAreaMarkdownRenderer.Area),
                Attr(attrs, LayoutAreaMarkdownRenderer.AreaId))));
            last = m.Index + m.Length;
        }
        if (last < html.Length)
            segments.Add((html[last..], null));
        // Only the kernel placeholder matched (all skipped) ⇒ a single html segment.
        return segments.Count == 0 ? new (string?, EmbeddedAreaRef?)[] { (html, null) } : segments;
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
    /// <param name="senderHub">The hub that posts the code submissions and observes responses.</param>
    /// <param name="meshService">The mesh service used to create the per-view Activity node.</param>
    /// <param name="activityAddress">The address of the Activity hub that hosts the kernel and receives submissions.</param>
    /// <param name="ownerPath">The owning node path under which the Activity is created; must be non-empty for a routable activity.</param>
    /// <param name="kernelId">Identifier used to name the Activity node and its path.</param>
    /// <param name="submissions">The code submissions to post once the Activity is routable.</param>
    /// <param name="onReady">Invoked ONCE, on the create/subscribe thread, the moment the per-view
    /// Activity node has been created AND become routable — i.e. it is now safe for the GUI to embed
    /// the live kernel area and subscribe. The Blazor views pass a callback that flips their
    /// <c>kernelReady</c> flag and re-renders, so the <c>LayoutAreaView</c> subscription is opened
    /// only AFTER the activity exists (closing the subscribe-before-create race — see
    /// <see cref="RenderKernelResultAreas"/>). Never fires if the activity fails to become routable;
    /// the view then keeps showing the non-subscribing "starting" placeholder.</param>
    /// <param name="onError">Invoked (on the subscribe thread) if the Activity never becomes routable
    /// — i.e. create or routing failed and the submissions were NOT posted. A view that has shown a
    /// non-subscribing "starting" placeholder uses this to surface the failure to the user (and to the
    /// activity log) instead of leaving an eternal spinner. Optional; Blazor callers rely on the
    /// kernel-area gate and pass nothing.</param>
    public static void CreateActivityAndSubmit(
        IMessageHub senderHub,
        IMeshService meshService,
        Address activityAddress,
        string? ownerPath,
        string kernelId,
        IReadOnlyCollection<SubmitCodeRequest> submissions,
        Action? onReady = null,
        Action<Exception>? onError = null)
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

        // 🚨 Fail fast at the SOURCE. An empty owner produces a bare `_Activity/markdown-{id}` —
        // a top-level / ownerless activity with no per-node hub to route to, so the kernel
        // SubmitCodeRequest below and every progress subscriber NotFound-storm the router. The
        // markdown views must never call this without a real owner (they gate on owner and render
        // a notice instead — see DisableKernelPlaceholder); this throw is the backstop that turns
        // any future ownerless caller into a loud, hunt-friendly exception rather than a storm.
        ActivityNodeGuard.EnsureOwned(activityNode);

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
                _ =>
                {
                    // The activity is now routable. Signal the GUI it is safe to embed the live
                    // kernel area + subscribe (closing the subscribe-before-create race) BEFORE
                    // posting the submissions; the area stream replays current state to the late
                    // subscriber, so ordering vs. SubmitCode is not load-bearing.
                    onReady?.Invoke();
                    SubmitCode(senderHub, activityAddress, submissions);
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "Markdown kernel activity {Path} did not become routable; code submissions not posted",
                        activityPath);
                    onError?.Invoke(ex);
                });
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

/// <summary>
/// The result of rendering markdown: the produced HTML plus any extracted executable code submissions.
/// </summary>
/// <param name="Html">The rendered HTML (with kernel-address placeholders still embedded).</param>
/// <param name="CodeSubmissions">The executable code submissions found in the document, or null if none.</param>
public sealed record MarkdownRenderResult(
    string Html,
    IReadOnlyList<SubmitCodeRequest>? CodeSubmissions);
