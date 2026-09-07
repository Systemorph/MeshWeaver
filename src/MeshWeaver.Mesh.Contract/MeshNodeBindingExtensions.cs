using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The binding seam that makes a <b>node-bound DataContext</b> (see
/// <see cref="LayoutAreaReference.MeshNodePrefix"/>) read from and write straight back to a live
/// <see cref="MeshNode"/> via <c>hub.GetMeshNodeStream(path)</c> (the process-wide
/// <c>IMeshNodeStreamCache</c>) — ONE source of truth, no layout-area <c>/data</c> replica and no
/// debounced save-subscription.
///
/// <para>This is the CONTROL-LEVEL binding primitive: it lives next to
/// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(MeshWeaver.Data.IWorkspace,string)"/> (NOT in
/// the Blazor view layer) so it is a plain static extension testable without a Blazor render host. The
/// GUI seams <c>BlazorView.DataBind</c> (read) and <c>BlazorView.UpdatePointer</c> (write) branch here
/// when the DataContext is node-bound, so every form control inherits node binding for free; the Monaco
/// editor views (<c>MarkdownEditorView</c> / <c>CodeEditorView</c> / <c>NotebookEditorView</c>), which
/// bind their single <c>Value</c> pointer themselves rather than through <c>BlazorView.DataBind</c>,
/// call <see cref="IsNodeBound"/> + <see cref="Bind"/> directly.</para>
///
/// <para><b>Why it must NOT be resolved via the layout <c>Stream</c>:</b> a node-bound pointer is
/// <c>/$meshNode/{base64url(nodePath)}/{c|n}/…</c>. For node <c>AgenticPension</c> the path segment is
/// <c>QWdlbnRpY1BlbnNpb24</c>. <c>LayoutExtensions.GetStream&lt;T&gt;</c> treats the second pointer
/// segment as a JSON-encoded id and calls <c>JsonSerializer.Deserialize&lt;string&gt;(segment)</c> — a
/// bare Base64Url token is not a JSON-quoted string, so it throws <c>"'Q' is an invalid start of a
/// value"</c> and tears down the whole Blazor circuit. Routing through this seam reads the field off the
/// node instead, where the content actually lives.</para>
///
/// <para>Field-pointer resolution against the node JSON is <b>case-insensitive</b>, so a metadata DTO's
/// PascalCase pointer (<c>Name</c>, <c>Description</c>) and a content editor's camelCase pointer
/// (<c>harness</c>, <c>messageContent</c>) both bind without the caller having to know the JSON casing
/// of the target.</para>
/// </summary>
public static class MeshNodeBindingExtensions
{
    /// <summary>
    /// True when <paramref name="dataContext"/> is node-bound (see
    /// <see cref="LayoutAreaReference.MeshNodePrefix"/>) AND <paramref name="reference"/> is a relative
    /// field pointer — i.e. the value lives on the <see cref="MeshNode"/>, not in the layout-area
    /// <c>/data</c> store. The relative-pointer condition matches <c>BlazorView.DataBind</c>: an
    /// absolute (<c>/…</c>) pointer is a layout-area path and is never node-bound.
    /// </summary>
    public static bool IsNodeBound(string? dataContext, JsonPointerReference reference)
        => LayoutAreaReference.TryParseMeshNodeDataContext(dataContext) is not null
           && !reference.Pointer.StartsWith('/');

    /// <summary>
    /// Pure read: evaluates the field at <paramref name="reference"/> (optionally nested under
    /// <paramref name="subPath"/>) against <paramref name="node"/> — its <c>Content</c> JSON when
    /// <paramref name="bindContent"/> is <c>true</c>, otherwise the whole-node JSON — and returns the
    /// value as a <see cref="JsonElement"/> (or <c>null</c> when absent). No stream, no hub: this is the
    /// node-bound read logic in isolation, so it is unit-testable against an in-memory node.
    /// </summary>
    public static JsonElement? ResolveField(
        MeshNode node, bool bindContent, string? subPath, JsonPointerReference reference,
        JsonSerializerOptions options)
        => EvaluateField(BindingRoot(node, bindContent, options), Combine(subPath, reference.Pointer));

    /// <summary>
    /// Live stream of the value at <paramref name="reference"/> on the node at
    /// <paramref name="nodePath"/>. Emits the raw <see cref="JsonElement"/> (or <c>null</c> when the
    /// field is absent) so the caller's existing converter pipeline (<c>Hub.ConvertSingle</c> /
    /// <c>ConversionToValue</c>) deserializes it exactly as it would a <c>/data</c> value. Stays
    /// subscribed for the component lifetime — no <c>.Take(1)</c>.
    ///
    /// <para>🚨 <b>THE BOUND NODE MAY NOT EXIST, AND THAT IS A FIRST-CLASS STATE — NOT A FAULT</b>
    /// (Systemorph/MeshWeaver#3517). Two live shapes reach here, and neither is a call-site
    /// mistake that could be designed away:</para>
    /// <list type="bullet">
    ///   <item><b>Deleted while the page was open.</b> The "Share ⇒ as email" form creates its
    ///     draft node up front (<c>EmailDraftNodeType.EnsureExists</c>) — the create-before-bind
    ///     discipline, already applied — and the user then deleted the document's whole
    ///     <c>_Draft</c> subtree with the tab still showing the form. No amount of call-site care
    ///     prevents that: any bound node can be deleted from under a live view.</item>
    ///   <item><b>Not written yet, deliberately.</b> A course quiz binds the learner's
    ///     <c>{user}/_Answers/…</c> node, which is created by the FIRST answer. Creating it on
    ///     render — so the binding always has a target — would write a node for every learner who
    ///     merely looked at the page.</item>
    /// </list>
    ///
    /// <para>So the read is composed the way <c>Doc/Architecture/CqrsAndContentAccess</c> →
    /// "An OPTIONAL node" requires: <b>a query answers WHETHER the node is there, the owner's
    /// stream answers WHAT it says.</b> A bare point read of an absent path is a framework defect,
    /// not merely a slow one — routing answers an authoritative <c>NotFound</c> that TERMINATES the
    /// stream, and that NotFound opens <c>MeshNodeStreamCache</c>'s storm-breaker window on the
    /// path, which fast-fails WRITES to it as well. #3517 is what that looks like from outside: 473
    /// <c>fail:</c> lines over four days on all five pods, 3–5 per render pass, from two unrelated
    /// spaces, because the view re-attempted the point read on every render and the breaker handed
    /// back the same cached exception each time.</para>
    ///
    /// <para>🚨 <b>A <c>catch</c> here would have been worse than the noise.</b> Swallowing the
    /// <c>DeliveryFailureException</c> hides the fault AND leaves the breaker open on the path — so
    /// the read goes on suppressing the write the form is about to make. The gate is the fix
    /// because it means the NotFound is never MINTED.</para>
    ///
    /// <para><b>The gate's shape, and why each part is the way it is:</b> an EXACT-path query
    /// (<c>path:{x}</c>, no <c>scope:</c> qualifier ⇒ <c>QueryScope.Exact</c>) whose contract for an
    /// absent path is "zero rows, no error" — empty-on-absent, no routing NotFound, no breaker
    /// window; the same instrument <c>ActivityRunner</c>, <c>MarkdownViewLogic</c> and
    /// <c>TrackActivity</c> already gate on. It is LIVE (a snapshot per emission, not a one-shot),
    /// so a node created or deleted later flips the gate and the binding follows without a
    /// re-render — no <c>.Take(1)</c> anywhere on the value path. And it runs
    /// <see cref="MeshQueryRequest.AsSystem"/>: the gate decides only EXISTENCE, the CONTENT read
    /// below is still row-level-security-gated by the owner exactly as before, and a query filtered
    /// by an identity that failed to resolve would answer "absent" for a node the viewer can see —
    /// an empty control indistinguishable from a real absence, which is the failure this repo bans
    /// and the reason <see cref="MeshQueryRequest.UserId"/> documents it as its own worst
    /// defect.</para>
    ///
    /// <para>🚨 <b>Bounded on the FIRST value only</b> (<see cref="ReadBudget"/>, 10 s), and the
    /// budget sits on the CONTENT leg — the read of a node the gate has already proven exists —
    /// never on the gate. Putting it outside would let the immediate "absent ⇒ null" emission
    /// satisfy the budget and silence the case it exists for: the owning hub unreachable / still
    /// starting, so the hydrating <c>SubscribeRequest</c> burns the hub's whole 60 s
    /// <c>RequestTimeout</c> before the fault reaches the view (Systemorph/MeshWeaver#1748 —
    /// <c>"No response received in hub cache/… → target Posts/RobertHaircuts"</c>).</para>
    ///
    /// <para><b>It degrades rather than errors, deliberately.</b> An error would tear the
    /// subscription down, and a hub that is merely slow — a cold NodeType compile legitimately
    /// outruns any interactive budget — would then never populate the control at all. Instead the
    /// budget emits the same <c>null</c> the binding emits for an absent field, so the control
    /// draws, AND the subscription stays live so a late value replaces it. The degradation is
    /// logged at Warning naming the node, the field and the budget — it is never silent.</para>
    /// </summary>
    /// <param name="hub">The hub whose node stream is read.</param>
    /// <param name="nodePath">The node to bind against.</param>
    /// <param name="bindContent">Bind against the node's Content (true) or the whole node (false).</param>
    /// <param name="subPath">Optional content sub-path the field pointer is nested under.</param>
    /// <param name="reference">The field pointer.</param>
    /// <param name="firstValueBudget">
    /// How long to wait for the FIRST value before drawing the control with no value;
    /// <see cref="ReadBudget.Default"/> when omitted. Only the first value is bounded — an idle,
    /// healthy binding is never cut short.
    /// </param>
    /// <param name="scheduler">Clock for the budget — pass a <c>TestScheduler</c> to drive it in
    /// virtual time.</param>
    /// <returns>The live field stream.</returns>
    public static IObservable<object?> Bind(
        IMessageHub hub, string nodePath, bool bindContent, string? subPath, JsonPointerReference reference,
        TimeSpan? firstValueBudget = null,
        IScheduler? scheduler = null)
    {
        var pointer = Combine(subPath, reference.Pointer);
        var content = Observable.Defer(() =>
            FieldStream(hub, nodePath, bindContent, pointer, firstValueBudget, scheduler));

        // No query surface on this hub (a minimal fixture, a client with no IMeshService) ⇒ the
        // gate cannot be asked, so the read stays exactly what it was. Degrading to the PREVIOUS
        // behaviour is the only honest fallback: refusing to bind would break every such host, and
        // pretending "absent" would blank every control on it.
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return content;

        var announcedAbsent = 0;
        return Exists(meshService, nodePath)
            .Select(exists => exists
                ? content
                : Observable.Defer(() =>
                {
                    if (Interlocked.Exchange(ref announcedAbsent, 1) == 0)
                        ReadBudget.Logger(hub)?.LogDebug(
                            "MeshNodeBinding: '{Field}' is bound to {Path}, which does not exist — "
                            + "drawing the control empty and watching for it to appear. This is the "
                            + "OPTIONAL-node state, not a fault; the point read that would have "
                            + "NotFound-stormed the path was never issued (#3517).",
                            pointer, nodePath);
                    return Observable.Return<object?>(null);
                }))
            .Switch()
            // Outside the Switch: an existence flip that re-opens the content leg must not re-fire
            // the setter with the value the control already shows.
            .DistinctUntilChanged(JsonElementValueComparer.Instance);
    }

    /// <summary>
    /// LIVE existence of the node at <paramref name="nodePath"/> — <c>true</c> while the index
    /// carries a row for exactly that path, <c>false</c> while it does not.
    ///
    /// <para>The index TRAILS the durable store, which is what makes it sound as a gate in this
    /// direction: "the index has seen it" implies "the store has it", so the point read opened on
    /// a <c>true</c> can never be early. The lag that makes a query useless for CONTENT is exactly
    /// what makes it safe for EXISTENCE.</para>
    ///
    /// <para>🚨 <c>StringComparison.Ordinal</c>, never <c>OrdinalIgnoreCase</c>: mesh paths are
    /// case-SENSITIVE, and a case-insensitive match would let a DIFFERENT node satisfy the gate —
    /// re-minting the very NotFound the gate exists to prevent.</para>
    /// </summary>
    private static IObservable<bool> Exists(IMeshService meshService, string nodePath)
        => meshService
            .Query(MeshQueryRequest.FromQuery($"path:{nodePath}").AsSystem())
            .Select(rows => rows.Any(r => string.Equals(r.Path, nodePath, StringComparison.Ordinal)))
            .DistinctUntilChanged();

    /// <summary>
    /// The CONTENT leg: the owner's authoritative live stream for a node the gate has proven
    /// exists, projected onto the bound field and bounded on its first emission. Stays subscribed
    /// for the component lifetime — no <c>.Take(1)</c>, so later writes to the node keep arriving.
    /// </summary>
    private static IObservable<object?> FieldStream(
        IMessageHub hub, string nodePath, bool bindContent, string pointer,
        TimeSpan? firstValueBudget, IScheduler? scheduler)
    {
        var options = hub.JsonSerializerOptions;
        return hub.GetMeshNodeStream(nodePath)
            .Where(node => node is not null)
            .Select(node => (object?)EvaluateField(BindingRoot(node, bindContent, options), pointer))
            .DegradeIfNoFirstEmission(
                fallback: null,
                onDegraded: failure => ReadBudget.Logger(hub)?.LogWarning(
                    "MeshNodeBinding: '{Field}' on {Path} has no value yet — drawing the control "
                    + "empty and staying subscribed. {Reason}",
                    pointer, nodePath, failure.Message),
                reader: hub,
                target: nodePath,
                what: $"field '{pointer}'",
                budget: firstValueBudget,
                scheduler: scheduler);
    }

    /// <summary>
    /// Writes <paramref name="value"/> into the field at <paramref name="reference"/> on the node at
    /// <paramref name="nodePath"/> via a per-field read-modify-write through the node stream. Sets
    /// ONLY the edited field; everything else on the node (and its Content) is preserved. Cold —
    /// subscribes here with explicit error logging (the framework's cold-observable + AccessContext
    /// propagation rules apply on <c>.Subscribe()</c>).
    /// </summary>
    public static void Write(
        IMessageHub hub, ILogger logger, string nodePath, bool bindContent, string? subPath,
        JsonPointerReference reference, object? value, Action<Exception>? onError = null)
    {
        var options = hub.JsonSerializerOptions;
        var valueNode = value is null ? null : JsonSerializer.SerializeToNode(value, options);
        var pointer = Combine(subPath, reference.Pointer);

        hub.GetMeshNodeStream(nodePath)
            .Update(node =>
            {
                if (bindContent)
                {
                    var content = ToJsonObject(node.Content, options) ?? new JsonObject();
                    SetField(content, pointer, valueNode);
                    return node with { Content = JsonSerializer.Deserialize<object>(content.ToJsonString(), options) };
                }

                // Whole-node mode: top-level node fields (Name/Description/Icon/Category/Order) plus
                // an optional nested content/… path. Round-trip the node through JSON, patch the
                // field, deserialize back — so an arbitrary form pointer maps onto the node's own
                // JSON shape without the editor hand-mapping every property.
                var nodeObj = JsonSerializer.SerializeToNode(node, options) as JsonObject ?? new JsonObject();
                SetField(nodeObj, pointer, valueNode);
                return JsonSerializer.Deserialize<MeshNode>(nodeObj.ToJsonString(), options) ?? node;
            })
            .Subscribe(
                _ => { },
                ex =>
                {
                    logger.LogWarning(ex,
                        "MeshNodeBinding: write failed for {Path} field {Field}", nodePath, pointer);
                    // Surface to the caller (the Blazor view pops a modal) instead of swallowing —
                    // a swallowed combobox/selection write is the silent "screen disappears on select".
                    onError?.Invoke(ex);
                });
    }

    /// <summary>Joins an optional content sub-path (e.g. <c>"composer"</c>) with a field pointer
    /// (e.g. <c>"harness"</c>) into a single JSON-pointer-style path (<c>"composer/harness"</c>).</summary>
    private static string Combine(string? subPath, string pointer)
        => string.IsNullOrEmpty(subPath) ? pointer : $"{subPath}/{pointer.TrimStart('/')}";

    /// <summary>The JSON object the form's field pointers resolve against: the node's Content
    /// (content mode) or the whole node (fields mode).</summary>
    private static JsonObject? BindingRoot(MeshNode node, bool bindContent, JsonSerializerOptions options)
        => bindContent
            ? ToJsonObject(node.Content, options)
            : JsonSerializer.SerializeToNode(node, options) as JsonObject;

    private static JsonObject? ToJsonObject(object? content, JsonSerializerOptions options)
    {
        if (content is null) return null;
        try
        {
            return content is JsonElement je
                ? JsonNode.Parse(je.GetRawText()) as JsonObject
                : JsonSerializer.SerializeToNode(content, options) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluates a JSON-pointer-style field path (supports nested <c>a/b</c>) against
    /// <paramref name="root"/>, case-insensitively, returning the value as a <see cref="JsonElement"/>
    /// (or <c>null</c> when any segment is missing). Leading <c>/</c> is tolerated.
    /// </summary>
    private static JsonElement? EvaluateField(JsonObject? root, string pointer)
    {
        if (root is null) return null;
        JsonNode? node = root;
        foreach (var segment in SplitPointer(pointer))
        {
            if (node is not JsonObject obj) return null;
            node = GetCaseInsensitive(obj, segment);
            if (node is null) return null;
        }
        if (ReferenceEquals(node, root)) return null; // empty pointer → no field value
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    /// <summary>Sets the field at <paramref name="pointer"/> on <paramref name="root"/> (creating
    /// intermediate objects), matching an existing key case-insensitively so we patch the SAME key
    /// the node already uses rather than adding a casing-variant duplicate.</summary>
    private static void SetField(JsonObject root, string pointer, JsonNode? value)
    {
        var segments = SplitPointer(pointer).ToArray();
        if (segments.Length == 0) return;
        var obj = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = ExistingKey(obj, segments[i]) ?? segments[i];
            if (obj[key] is JsonObject child)
            {
                obj = child;
            }
            else
            {
                var created = new JsonObject();
                obj[key] = created;
                obj = created;
            }
        }
        var last = ExistingKey(obj, segments[^1]) ?? segments[^1];
        obj[last] = value;
    }

    private static IEnumerable<string> SplitPointer(string pointer)
        => (pointer ?? string.Empty)
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Replace("~1", "/").Replace("~0", "~"));

    private static JsonNode? GetCaseInsensitive(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var exact))
            return exact;
        foreach (var kvp in obj)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return null;
    }

    private static string? ExistingKey(JsonObject obj, string key)
    {
        if (obj.ContainsKey(key)) return key;
        foreach (var kvp in obj)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        return null;
    }

    /// <summary>Equality over the boxed <see cref="JsonElement"/>s the binding emits, so an echo of
    /// our own write (or a no-op re-emission) is filtered by <c>DistinctUntilChanged</c> rather than
    /// re-running the setter every node tick.</summary>
    private sealed class JsonElementValueComparer : IEqualityComparer<object?>
    {
        public static readonly JsonElementValueComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;
            if (x is JsonElement xe && y is JsonElement ye)
                return xe.GetRawText() == ye.GetRawText();
            return x.Equals(y);
        }

        public int GetHashCode(object? obj)
            => obj is JsonElement je ? je.GetRawText().GetHashCode() : obj?.GetHashCode() ?? 0;
    }
}
