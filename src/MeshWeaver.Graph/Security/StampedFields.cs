using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.Validation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// The stamped fields a NodeType declares (<see cref="NodeTypeDefinition.StampedFields"/>), as read
/// for one type: the field names, or the fault that kept them from being read.
/// </summary>
/// <param name="Fields">The declared content field names; empty when the type declares none.</param>
/// <param name="Fault">Set when the declaration could not be read — never folded into "none".</param>
public sealed record StampedFieldDeclaration(ImmutableList<string> Fields, Exception? Fault = null)
{
    /// <summary>A type that declares no stamped field.</summary>
    public static StampedFieldDeclaration None { get; } = new(ImmutableList<string>.Empty);
}

/// <summary>
/// 🚨 <b>A stamped field is written by the node's own hub and by nothing else</b> — the platform rule
/// behind <see cref="NodeTypeDefinition.StampedFields"/> (Doc/Architecture/StampedFields).
///
/// <para>A NodeType whose code stamps a field that something downstream TRUSTS — who confirmed an eval
/// case, and therefore whose identity a headless run takes — cannot rely on its own stamp alone while
/// anyone holding Update on the node can patch the stored value afterwards. So the type declares the
/// field and the platform refuses every CLIENT write that changes it:</para>
/// <list type="bullet">
///   <item><b>Cross-hub writes</b> — a <see cref="PatchDataRequest"/> (every
///     <c>GetMeshNodeStream(path).Update</c> from another hub, MCP <c>update</c>/<c>patch</c>/
///     <c>edit_content</c>, an upsert's update branch) reaching the node's hub from any other address —
///     refused by a delivery step on the node's hub (<see cref="AddStampedFieldGuard"/>) BEFORE the
///     merge runs, naming the field; a synced subscriber's write-back — refused by
///     <see cref="StampedFieldDataValidator"/>, the data layer's validation seam on the same hub.</item>
///   <item><b>Creates</b> — a create carrying a non-empty stamped field is refused by
///     <see cref="StampedFieldCreateValidator"/>: the field does not exist until the node's hub
///     stamps it.</item>
/// </list>
/// <para>The owning hub's own writes (<c>GetMeshNodeStream().Update</c> on its own node) never
/// travel as a message, so they are the one path that sets the field. Refused, never stripped: a
/// silent strip would tell the writer its value landed.</para>
/// </summary>
public static class StampedFields
{
    /// <summary>
    /// Reads the stamped fields <paramref name="nodeType"/> declares: off the static registry, else off
    /// the durable NodeType row (<see cref="IStorageAdapter.ReadMany"/> — the repair-free read seam, and
    /// no activation of the type's own hub). Faults when the row cannot be read; an ABSENT row declares
    /// nothing.
    /// </summary>
    /// <param name="hub">The hub whose services resolve the declaration.</param>
    /// <param name="nodeType">The NodeType path.</param>
    /// <returns>One declaration; an error when the store faulted.</returns>
    public static IObservable<StampedFieldDeclaration> Resolve(IMessageHub hub, string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType)
            || string.Equals(nodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return(StampedFieldDeclaration.None);

        var options = hub.JsonSerializerOptions;
        if (hub.ServiceProvider.FindStaticNode(nodeType) is { } staticType)
            return Observable.Return(FromDefinitionRow(staticType, options, requireNodeTypeRow: false));

        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(StampedFieldDeclaration.None);

        return Observable.Defer(() => storage.ReadMany([nodeType], options))
            .Take(1)
            .Select(row => FromDefinitionRow(row, options, requireNodeTypeRow: true))
            .DefaultIfEmpty(StampedFieldDeclaration.None);
    }

    private static StampedFieldDeclaration FromDefinitionRow(
        MeshNode? row, JsonSerializerOptions options, bool requireNodeTypeRow)
    {
        if (row is null)
            return StampedFieldDeclaration.None;
        if (requireNodeTypeRow
            && !string.Equals(row.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase))
            return StampedFieldDeclaration.None;
        var declared = row.ContentAs<NodeTypeDefinition>(options)?.StampedFields;
        if (declared is null || declared.Count == 0)
            return StampedFieldDeclaration.None;
        return new(declared
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableList());
    }

    /// <summary>
    /// The stamped fields an RFC 7396 merge patch of a whole node would CHANGE on
    /// <paramref name="liveContent"/>. A field the patch does not name is untouched; a field it names
    /// with the live value is a no-op; a removal (JSON <c>null</c>) of a non-empty live value, a
    /// different value, or a string splice is a change. A patch that removes or replaces the whole
    /// <c>content</c> changes every field whose live value is non-empty. Pure.
    /// </summary>
    /// <param name="liveContent">The node's content now (null when absent).</param>
    /// <param name="patch">The node-level merge patch.</param>
    /// <param name="contentKey">The JSON name of <c>MeshNode.Content</c>.</param>
    /// <param name="fields">The declared stamped fields.</param>
    /// <returns>The fields the patch would change, in declaration order.</returns>
    public static ImmutableList<string> ChangedByMergePatch(
        JsonObject? liveContent, JsonObject patch, string contentKey, IReadOnlyList<string> fields)
    {
        if (!TryGet(patch, contentKey, out var contentPatch))
            return ImmutableList<string>.Empty;
        if (contentPatch is not JsonObject contentObject)
            // content removed (null) or replaced by a non-object: whatever was stamped is gone.
            return fields.Where(f => !IsEmpty(Get(liveContent, f))).ToImmutableList();

        var changed = ImmutableList<string>.Empty;
        foreach (var field in fields)
        {
            if (!TryGet(contentObject, field, out var patchValue))
                continue;
            var live = Get(liveContent, field);
            var isChange = PatchStringSplice.TryDecode(patchValue, out _)
                || (patchValue is null ? !IsEmpty(live) : !JsonNode.DeepEquals(patchValue, live));
            if (isChange)
                changed = changed.Add(field);
        }
        return changed;
    }

    /// <summary>The stamped fields whose value differs between two contents (absent ≡ null). Pure.</summary>
    /// <param name="before">The content before the write.</param>
    /// <param name="after">The content the write proposes.</param>
    /// <param name="fields">The declared stamped fields.</param>
    /// <returns>The changed fields, in declaration order.</returns>
    public static ImmutableList<string> Changed(
        JsonObject? before, JsonObject? after, IReadOnlyList<string> fields)
        => fields
            .Where(f => !JsonNode.DeepEquals(Get(before, f), Get(after, f)))
            .ToImmutableList();

    /// <summary>
    /// The stamped fields that carry a value on <paramref name="content"/> — what a create may not
    /// bring: null, an empty string, <c>false</c>, zero and an empty array/object count as no value.
    /// Pure.
    /// </summary>
    /// <param name="content">The proposed content.</param>
    /// <param name="fields">The declared stamped fields.</param>
    /// <returns>The pre-filled fields, in declaration order.</returns>
    public static ImmutableList<string> Present(JsonObject? content, IReadOnlyList<string> fields)
        => fields.Where(f => !IsEmpty(Get(content, f))).ToImmutableList();

    /// <summary>
    /// The sentence a refused write carries: which field(s), on which node, and who may write them.
    /// </summary>
    /// <param name="path">The node's path.</param>
    /// <param name="nodeType">The node's type, which declares the fields.</param>
    /// <param name="fields">The refused fields.</param>
    /// <param name="isCreate">Whether the refused write is a create.</param>
    /// <returns>The refusal.</returns>
    public static string Refusal(string path, string? nodeType, IReadOnlyList<string> fields, bool isCreate)
    {
        var names = string.Join(", ", fields.Select(f => $"'{f}'"));
        var what = fields.Count == 1 ? "is a stamped field" : "are stamped fields";
        return isCreate
            ? $"Access denied: creating '{path}' with {names} was refused — {names} {what} of "
              + $"{nodeType ?? "its type"}, written only by the node's own hub after it exists. Create "
              + "the node without it; nothing was written."
            : $"Access denied: the write to {names} on '{path}' was refused — {names} {what} of "
              + $"{nodeType ?? "its type"}, written only by the node's own hub, never by a client patch. "
              + "Nothing was changed; leave the field out of the write.";
    }

    /// <summary>The content of <paramref name="node"/> as a JSON object (null when it has none).</summary>
    /// <param name="node">The node.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <returns>The content's JSON object, or null.</returns>
    public static JsonObject? ContentObject(MeshNode? node, JsonSerializerOptions options)
        => node?.Content switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Object } element => JsonNode.Parse(element.GetRawText()) as JsonObject,
            JsonObject json => json,
            { } typed => JsonSerializer.SerializeToNode(typed, typed.GetType(), options) as JsonObject,
        };

    /// <summary>
    /// Installs the stamped-field guard on a node hub: every <see cref="PatchDataRequest"/> on its own
    /// node that reaches it from ANOTHER address is checked against the stamped fields the hub's
    /// NodeType declares, and refused — naming the field — when it would change one. A synced
    /// subscriber's write-back answers to <see cref="StampedFieldDataValidator"/> on the same hub.
    ///
    /// <para>🚨 <b>The declaration is resolved while the hub INITIALIZES, never on a write.</b> It is
    /// one of the hub's buildup actions (<see cref="StampedFieldDeclarationHolder.Load"/>), so the
    /// framework's Initialize gate holds every message until it is settled; by the time the first
    /// client write reaches this step the verdict is computed synchronously on the hub's own turn and
    /// the write continues on THAT turn. Resolving it lazily on the first write held that write back
    /// and let it continue on the store's thread, outside the action block — where it could be
    /// reordered against the hub's later writes.</para>
    /// </summary>
    /// <param name="config">The node hub's configuration.</param>
    /// <returns>The configuration with the guard installed.</returns>
    public static MessageHubConfiguration AddStampedFieldGuard(this MessageHubConfiguration config)
        => config
            .WithServices(services => services
                .AddSingleton<StampedFieldDeclarationHolder>()
                // Clear on retype: the rebind watcher settles the new type's stamped fields through
                // the same holder before it recycles the hub.
                .AddSingleton<INodeRetypeSettlement>(sp => sp.GetRequiredService<StampedFieldDeclarationHolder>())
                .AddScoped<IDataValidator, StampedFieldDataValidator>())
            .WithInitialization(LoadDeclaration)
            .AddDeliveryPipeline(pipeline =>
            {
                var hub = pipeline.Hub;
                ILogger? logger = null;

                return pipeline.AddPipeline((delivery, ct, next) =>
                {
                    if (!IsClientPatchOfThisHub(hub, delivery))
                        return next.Invoke(delivery, ct);

                    logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Graph.StampedFields");

                    // Synchronous on this turn: the declaration was settled during initialization.
                    var holder = hub.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>();
                    var declared = holder.Current;
                    if (declared.Fault is { } fault)
                    {
                        holder.RetryAfterFault();
                        Refuse(hub, delivery, ErrorType.Unavailable, Unavailable(hub, fault), logger);
                        return Observable.Return(delivery.Forwarded());
                    }
                    var patchRequest = (PatchDataRequest)delivery.Message;
                    if (patchRequest.Reference is not MeshNodeReference)
                    {
                        // Any other reference (a JSON pointer, an entity, a collection…) reaches the
                        // same data source through its reducer's reverse patch, and its shape says
                        // nothing this guard can compare field by field. No caller in the platform
                        // patches a node that way; on a type that declares stamped fields it is refused.
                        if (declared.Fields.IsEmpty)
                            return next.Invoke(delivery, ct);
                        Refuse(hub, delivery, ErrorType.Unauthorized,
                            NotJudgeable(hub, patchRequest.Reference, declared.Fields), logger);
                        return Observable.Return(delivery.Forwarded());
                    }
                    if (holder.IsRebinding && RetypesOwnNode(hub, patchRequest))
                    {
                        Refuse(hub, delivery, ErrorType.Unavailable, Rebinding(hub), logger);
                        return Observable.Return(delivery.Forwarded());
                    }
                    if (VerdictFor(hub, patchRequest, declared) is { } refusal)
                    {
                        Refuse(hub, delivery, ErrorType.Unauthorized, refusal, logger);
                        return Observable.Return(delivery.Forwarded());
                    }
                    return next.Invoke(delivery, ct);
                });
            });

    /// <summary>The buildup action that settles the declaration before the hub accepts messages.</summary>
    private static IObservable<System.Reactive.Unit> LoadDeclaration(IMessageHub hub)
        => hub.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>().Load();

    /// <summary>
    /// A merge patch ADDRESSED to this hub's own node that came from anywhere but this hub. The owning
    /// hub's own writes never arrive as a message (they commit on its own data source), so the sender
    /// test is exact; a write merely passing through to a hosted hub is not this hub's to judge.
    /// </summary>
    private static bool IsClientPatchOfThisHub(IMessageHub hub, IMessageDelivery delivery)
    {
        if (delivery.Message is not PatchDataRequest)
            return false;
        var ownPath = hub.Address.Path;
        if (delivery.Target is { } target && !string.Equals(target.Path, ownPath, StringComparison.OrdinalIgnoreCase))
            return false;
        return delivery.Sender is null
            || !string.Equals(delivery.Sender.Path, ownPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The NodeType whose configuration this hub runs — the type that declares the fields.</summary>
    internal static string? OwnNodeType(IMessageHub hub)
        => hub.Configuration.Get<NodeTypePathHolder>()?.Path ?? LiveOwnNode(hub)?.NodeType;

    /// <summary>The hub's own node as its data source holds it now (null before it has loaded).</summary>
    internal static MeshNode? LiveOwnNode(IMessageHub hub)
    {
        var store = hub.GetWorkspace().DataContext.GetDataSourceForType(typeof(MeshNode))
            ?.GetStreamForPartition(null)?.Current?.Value;
        var ownPath = hub.Address.Path;
        return store?.Collections.GetValueOrDefault(nameof(MeshNode))?.Instances.Values
            .OfType<MeshNode>()
            .FirstOrDefault(n => string.Equals(n.Path, ownPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The refusal for a merge patch of the hub's own node, or null when it changes no stamped field.</summary>
    private static string? VerdictFor(IMessageHub hub, PatchDataRequest patchRequest, StampedFieldDeclaration declared)
    {
        if (declared.Fields.IsEmpty)
            return null;

        var options = hub.JsonSerializerOptions;
        var live = LiveOwnNode(hub);
        if (JsonNode.Parse(patchRequest.Patch.Content ?? "{}") is not JsonObject patch)
            return null; // not an object: the handler refuses it with its own error
        var contentKey = options.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.Content))
            ?? nameof(MeshNode.Content);
        var changed = ChangedByMergePatch(ContentObject(live, options), patch, contentKey, declared.Fields);
        return changed.IsEmpty
            ? null
            : Refusal(live?.Path ?? hub.Address.Path, live?.NodeType ?? OwnNodeType(hub), changed, isCreate: false);
    }

    /// <summary>
    /// The refusal for one entity of a data-change update on the hub's own node — a whole
    /// <see cref="MeshNode"/> or an <see cref="EntityDeltaUpdate"/> of it — or null when it changes no
    /// stamped field or is not the hub's own node.
    /// </summary>
    internal static string? VerdictForUpdate(IMessageHub hub, object entity, StampedFieldDeclaration declared)
    {
        if (declared.Fields.IsEmpty)
            return null;

        var options = hub.JsonSerializerOptions;
        var live = LiveOwnNode(hub);
        var path = live?.Path ?? hub.Address.Path;
        var after = entity switch
        {
            EntityDeltaUpdate delta when live is not null
                && delta.Collection == nameof(MeshNode)
                && (delta.Id == live.Id || IsOwn(delta.Id, live.Path))
                => EntityDelta.Apply(live, delta, options) as MeshNode,
            EntityDeltaUpdate => null,
            _ when entity.As<MeshNode>(options) is { } node && IsOwn(node.Path, path) => node,
            _ => null
        };
        if (after is null)
            return null;
        var changed = Changed(ContentObject(live, options), ContentObject(after, options), declared.Fields);
        return changed.IsEmpty ? null : Refusal(path, live?.NodeType ?? OwnNodeType(hub), changed, isCreate: false);
    }

    private static bool IsOwn(string? candidate, string ownPath)
        => string.Equals(candidate, ownPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a merge patch of the hub's own node changes its <c>nodeType</c>.</summary>
    private static bool RetypesOwnNode(IMessageHub hub, PatchDataRequest patchRequest)
    {
        if (JsonNode.Parse(patchRequest.Patch.Content ?? "{}") is not JsonObject patch)
            return false;
        var key = hub.JsonSerializerOptions.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.NodeType))
            ?? nameof(MeshNode.NodeType);
        if (!TryGet(patch, key, out var value))
            return false;
        var proposed = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return IsRetype(LiveOwnNode(hub)?.NodeType, proposed);
    }

    /// <summary>Whether a data-change entity of the hub's own node changes its <c>nodeType</c>.</summary>
    internal static bool RetypesOwnNode(IMessageHub hub, object entity)
    {
        var options = hub.JsonSerializerOptions;
        var live = LiveOwnNode(hub);
        if (live is null)
            return false;
        var after = entity switch
        {
            EntityDeltaUpdate delta when delta.Collection == nameof(MeshNode)
                && (delta.Id == live.Id || IsOwn(delta.Id, live.Path))
                => EntityDelta.Apply(live, delta, options) as MeshNode,
            EntityDeltaUpdate => null,
            _ when entity.As<MeshNode>(options) is { } node && IsOwn(node.Path, live.Path) => node,
            _ => null
        };
        return after is not null && IsRetype(live.NodeType, after.NodeType);
    }

    private static bool IsRetype(string? live, string? proposed)
        => !string.Equals(live ?? string.Empty, proposed ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="node"/> with every <paramref name="fields"/> entry that carries a value
    /// removed from its content (so it reads as the content type's default) — the same instance when
    /// none carries one. The content keeps its CLR shape: a typed record stays that record, raw JSON
    /// stays raw JSON. Pure.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="fields">The stamped fields to clear.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <returns>The node without values in those fields.</returns>
    public static MeshNode WithoutValues(MeshNode node, IReadOnlyList<string> fields, JsonSerializerOptions options)
    {
        var content = ContentObject(node, options);
        var present = Present(content, fields);
        if (content is null || present.IsEmpty)
            return node;
        var copy = (JsonObject)content.DeepClone();
        foreach (var field in present)
            foreach (var key in copy.Select(kvp => kvp.Key)
                         .Where(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)).ToImmutableList())
                copy.Remove(key);
        object? next = node.Content switch
        {
            JsonElement => JsonSerializer.SerializeToElement(copy, options),
            JsonObject => copy,
            { } typed => copy.Deserialize(typed.GetType(), options),
            null => null,
        };
        return node with { Content = next };
    }

    /// <summary>The refusal for a client patch of this hub's data through a reference other than the node's own.</summary>
    internal static string NotJudgeable(IMessageHub hub, WorkspaceReference reference, IReadOnlyList<string> fields)
        => $"Access denied: a patch of '{hub.Address.Path}' through a {reference.GetType().Name} was refused — its "
           + $"type declares stamped field(s) {string.Join(", ", fields.Select(f => $"'{f}'"))}, and only a "
           + "patch of the node itself (MeshNodeReference, i.e. GetMeshNodeStream(path).Update) can be checked "
           + "for them. Nothing was changed; patch the node instead.";

    internal static string Rebinding(IMessageHub hub)
        => $"Write to '{hub.Address.Path}' refused: the node was just retyped and this activation is handing "
           + "it to one bound to the new type, so a further change of its type cannot be judged here. "
           + "This is NOT a statement about the caller's rights. Retry shortly.";

    internal static string Unavailable(IMessageHub hub, Exception ex)
        => $"Write to '{hub.Address.Path}' refused: the stamped fields its type declares could not be read, "
           + "so whether this write changes one could not be decided. This is NOT a statement about the "
           + $"caller's rights. Retry shortly. Cause: {ex.GetType().Name}: {ex.Message}";

    private static void Refuse(
        IMessageHub hub, IMessageDelivery delivery, ErrorType errorType, string message, ILogger? logger)
    {
        logger?.LogWarning(
            "StampedFields: refused {MessageType} from {Sender} on {Hub}: {Message}",
            delivery.Message.GetType().Name, delivery.Sender, hub.Address, message);
        hub.Post(new DeliveryFailure(delivery) { ErrorType = errorType, Message = message },
            o => o.ResponseFor(delivery));
    }

    private static bool TryGet(JsonObject obj, string key, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(key, out value))
            return true;
        foreach (var kvp in obj)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        value = null;
        return false;
    }

    private static JsonNode? Get(JsonObject? obj, string key)
        => obj is not null && TryGet(obj, key, out var value) ? value : null;

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var s) => string.IsNullOrEmpty(s),
        JsonValue scalar when scalar.TryGetValue<bool>(out var b) => !b,
        JsonValue scalar when scalar.TryGetValue<double>(out var d) => d == 0,
        _ => false
    };
}

/// <summary>
/// Refuses a CREATE whose content carries a non-empty stamped field
/// (<see cref="NodeTypeDefinition.StampedFields"/>): the field does not exist until the node's own hub
/// stamps it, so a creator that brings one is forging it. Fails closed when the type's declaration
/// cannot be read. See <see cref="StampedFields"/>.
/// </summary>
/// <param name="hub">The validating hub.</param>
public sealed class StampedFieldCreateValidator(IMessageHub hub) : INodeValidator
{
    /// <summary>Create only — updates are judged on the node's own hub (<see cref="StampedFields.AddStampedFieldGuard"/>).</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (node.Content is null || string.IsNullOrEmpty(node.NodeType))
            return Observable.Return(NodeValidationResult.Valid());

        return StampedFields.Resolve(hub, node.NodeType)
            .Select(declared =>
            {
                if (declared.Fields.IsEmpty)
                    return NodeValidationResult.Valid();
                var present = StampedFields.Present(
                    StampedFields.ContentObject(node, hub.JsonSerializerOptions), declared.Fields);
                return present.IsEmpty
                    ? NodeValidationResult.Valid()
                    : NodeValidationResult.Unauthorized(
                        StampedFields.Refusal(node.Path, node.NodeType, present, isCreate: true));
            })
            .Catch<NodeValidationResult, Exception>(ex => Observable.Return(NodeValidationResult.Unavailable(
                $"Creating '{node.Path}' was refused: the stamped fields of {node.NodeType} could not be read, "
                + $"so whether the content forges one could not be decided. Retry shortly. Cause: "
                + $"{ex.GetType().Name}: {ex.Message}")));
    }
}

/// <summary>
/// The stamped-field declaration of the type a node hub runs, resolved while the hub initializes
/// (<see cref="Load"/>, a buildup action — so the Initialize gate holds every message until it is
/// settled) and kept for the hub's life. Shared by the hub's delivery guard and its data validator,
/// both of which read <see cref="Current"/> synchronously on the hub's own turn. Registered as a
/// singleton of the node hub's own container, so its lifetime is the hub's.
///
/// <para>A read that faults is kept as a fault, never folded into "declares nothing": every write it
/// would judge is refused as <c>Unavailable</c> on its own turn, and the first such refusal starts
/// ONE re-read that replaces the fault when it settles. No write ever waits for it.</para>
/// </summary>
/// <param name="hub">The node hub.</param>
public sealed class StampedFieldDeclarationHolder(IMessageHub hub) : IDisposable, INodeRetypeSettlement
{
    private static readonly StampedFieldDeclaration NotYetResolved = new(
        ImmutableList<string>.Empty,
        new InvalidOperationException("the stamped-field declaration has not been resolved for this activation"));

    private volatile StampedFieldDeclaration current = NotYetResolved;
    private int retrying;
    private int rebinding;
    private readonly System.Reactive.Disposables.SerialDisposable retry = new();

    /// <summary>
    /// Set once this activation has started settling a retype of its node: from then on it is on its
    /// way out (the rebind recycle follows the settlement), and a client write that would retype the
    /// node AGAIN is refused as retryable — the retry reaches the activation bound to the new type,
    /// which settles that retype in turn. Without it a second retype landing after the clear and
    /// before the recycle would hand the next activation a value its type never judged.
    /// </summary>
    public bool IsRebinding => Volatile.Read(ref rebinding) == 1;

    /// <summary>
    /// 🚨 <b>Clear on retype.</b> The node was retyped while this activation — bound to its previous
    /// type — served it; the rebind watcher calls this post-commit, before it recycles the hub. When
    /// the type the node carries NOW differs from the bound one and declares stamped fields, every
    /// such field that carries a value is cleared by THIS hub's own write (it never travels as a
    /// message, so no guard judges it) and flushed durably; the guard and the data validator judge
    /// every later client write against the new type's declaration. So the activation bound to the
    /// new type starts from a node in which only its own code can set those fields.
    ///
    /// <para>Off the write path: the retype itself was never held back — the declaration is read
    /// here, after the commit, exactly as a hub start reads its own. A read or clear that faults
    /// keeps a FAULTED declaration (every client write refused as <c>Unavailable</c>) and errors, so
    /// the watcher does not recycle; the first refused write re-runs the settlement, which recycles
    /// the hub itself once it lands.</para>
    /// </summary>
    IObservable<System.Reactive.Unit> INodeRetypeSettlement.Settle(string? newNodeType)
        => Observable.Defer(() =>
        {
            Volatile.Write(ref rebinding, 1);
            return SettleRetype();
        });

    private string? BoundNodeType => hub.Configuration.Get<NodeTypePathHolder>()?.Path;

    private IObservable<System.Reactive.Unit> SettleRetype()
        => Observable.Defer(() =>
        {
            // The type the node carries NOW, not the one the change event named: a second retype
            // committed before this settlement started is settled here too (later ones are refused).
            var nodeType = StampedFields.LiveOwnNode(hub)?.NodeType;
            if (string.Equals(nodeType ?? string.Empty, BoundNodeType ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                // Retyped back: every write in between was judged against this very declaration.
                return Observable.Return(System.Reactive.Unit.Default);
            return Read(nodeType)
                .SelectMany(declared =>
                {
                    if (declared.Fault is { } fault)
                        return Observable.Throw<System.Reactive.Unit>(fault);
                    current = declared;
                    return declared.Fields.IsEmpty
                        ? Observable.Return(System.Reactive.Unit.Default)
                        : ClearOwn(declared.Fields);
                })
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    current = new StampedFieldDeclaration(ImmutableList<string>.Empty, ex);
                    return Observable.Throw<System.Reactive.Unit>(ex);
                });
        });

    /// <summary>
    /// Clears every <paramref name="fields"/> entry that carries a value, as this hub's OWN write
    /// (run as System — it is the node's maintenance by its own hub, not anyone's edit), then flushes
    /// it durably so the next activation, which reads the node back from storage, sees it cleared.
    /// </summary>
    private IObservable<System.Reactive.Unit> ClearOwn(ImmutableList<string> fields)
    {
        var options = hub.JsonSerializerOptions;
        var cleared = 0;
        MeshNode Clear(MeshNode node)
        {
            var next = StampedFields.WithoutValues(node, fields, options);
            if (!ReferenceEquals(next, node))
                Volatile.Write(ref cleared, 1);
            return next;
        }
        IObservable<MeshNode> Write() => hub.GetWorkspace().GetMeshNodeStream().Update(Clear).Take(1);
        var access = hub.ServiceProvider.GetService<AccessService>();
        var flush = hub.ServiceProvider.GetService<IPostCommitFlush>();
        return (access is null ? Write() : access.RunAsSystem(Write))
            .SelectMany(node => Volatile.Read(ref cleared) == 1 && flush is not null
                ? flush.Flush(node).Take(1).Select(_ => System.Reactive.Unit.Default)
                    .DefaultIfEmpty(System.Reactive.Unit.Default)
                : Observable.Return(System.Reactive.Unit.Default))
            .DefaultIfEmpty(System.Reactive.Unit.Default);
    }

    /// <summary>
    /// The declaration as settled for this activation. Before <see cref="Load"/> has settled it — which
    /// a message the Initialize gate holds can never observe — it reads as a fault, so a write is
    /// refused rather than judged against "declares nothing".
    /// </summary>
    public StampedFieldDeclaration Current => current;

    /// <summary>
    /// Resolves the declaration for the type this hub was bound to and stores it, fault included.
    /// Never errors: a fault is a stored value, so the hub still starts and refuses what it cannot judge.
    /// </summary>
    /// <returns>One value once the declaration is stored.</returns>
    public IObservable<System.Reactive.Unit> Load()
        => Read().Do(d => current = d).Select(_ => System.Reactive.Unit.Default);

    /// <summary>Starts one re-read after a faulted resolution; a re-read already running is not doubled.</summary>
    public void RetryAfterFault()
    {
        if (Interlocked.CompareExchange(ref retrying, 1, 0) != 0)
            return;
        if (IsRebinding)
        {
            // The fault is a retype settlement that did not land; re-run it, and recycle once it
            // has — the watcher that would have recycled gave up on the first attempt's error.
            retry.Disposable = SettleRetype().Subscribe(
                _ =>
                {
                    Interlocked.Exchange(ref retrying, 0);
                    if (!hub.IsDisposing)
                        hub.RecycleSelfAfterAcceptedWork(
                            $"StampedFields: node '{hub.Address.Path}' was retyped away from "
                            + $"'{BoundNodeType ?? "(none)"}' and its stamped fields are now settled",
                            hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Graph.StampedFields"));
                },
                _ => Interlocked.Exchange(ref retrying, 0));
            return;
        }
        retry.Disposable = Read().Subscribe(
            d =>
            {
                current = d;
                Interlocked.Exchange(ref retrying, 0);
            },
            // Read() turns a fault into a stored value; should anything still error, the declaration
            // stays faulted (fail-closed) and the next refusal starts another re-read.
            ex =>
            {
                current = new StampedFieldDeclaration(ImmutableList<string>.Empty, ex);
                Interlocked.Exchange(ref retrying, 0);
            });
    }

    // Bound to the NodeType this hub's configuration was built for (NodeTypePathHolder): the type is
    // known before any data loads, so nothing here waits for the hub's own node.
    private IObservable<StampedFieldDeclaration> Read() => Read(BoundNodeType);

    private IObservable<StampedFieldDeclaration> Read(string? nodeType)
        => StampedFields.Resolve(hub, nodeType)
            .Take(1)
            .DefaultIfEmpty(StampedFieldDeclaration.None)
            .Catch<StampedFieldDeclaration, Exception>(ex =>
                Observable.Return(new StampedFieldDeclaration(ImmutableList<string>.Empty, ex)));

    /// <inheritdoc />
    public void Dispose() => retry.Dispose();
}

/// <summary>
/// The data layer's half of the stamped-field guard: an update arriving as a data change (a synced
/// subscriber's write-back of the node) that would change a stamped field of the hub's own node is
/// refused, naming the field. The data layer runs its validators only for a change that ARRIVES as a
/// message, so the owning hub's own writes never meet this. Creations answer to
/// <see cref="StampedFieldCreateValidator"/> through the create-validator chain. See
/// <see cref="StampedFields"/>.
/// </summary>
/// <param name="hub">The node hub.</param>
internal sealed class StampedFieldDataValidator(IMessageHub hub) : IDataValidator
{
    /// <inheritdoc />
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Update, DataOperation.Create];

    /// <inheritdoc />
    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Operation is not (DataOperation.Update or DataOperation.Create))
            return Observable.Return(DataValidationResult.Valid());
        // A creation entry for a node that does not exist yet answers to the create-validator chain.
        // One for the hub's OWN, existing node is applied as a REPLACE by the change pipeline, so it
        // is judged exactly like an update — otherwise it could clear or overwrite the owner's stamp.
        if (context.Operation == DataOperation.Create && StampedFields.LiveOwnNode(hub) is null)
            return Observable.Return(DataValidationResult.Valid());

        // Settled while the hub initialized — read synchronously, never awaited.
        var holder = hub.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>();
        var declared = holder.Current;
        if (declared.Fault is { } fault)
        {
            holder.RetryAfterFault();
            return Observable.Return(DataValidationResult.Invalid(
                StampedFields.Unavailable(hub, fault), DataValidationRejectionReason.Unknown));
        }
        if (holder.IsRebinding && StampedFields.RetypesOwnNode(hub, context.Entity))
            return Observable.Return(DataValidationResult.Invalid(
                StampedFields.Rebinding(hub), DataValidationRejectionReason.Unknown));
        return Observable.Return(StampedFields.VerdictForUpdate(hub, context.Entity, declared) is { } refusal
            ? DataValidationResult.Invalid(refusal, DataValidationRejectionReason.Unauthorized)
            : DataValidationResult.Valid());
    }
}
