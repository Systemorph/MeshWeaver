using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
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
///     <c>edit_content</c>, an upsert's update branch) and a <see cref="DataChangeRequest"/> (a synced
///     subscriber's write-back) reaching the node's hub from any other address — refused by a delivery
///     step on the node's hub (<see cref="AddStampedFieldGuard"/>) BEFORE the merge runs, naming the
///     field.</item>
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
    /// node and every <see cref="DataChangeRequest"/> that reaches it from ANOTHER address is checked
    /// against the stamped fields the hub's NodeType declares, and refused — naming the field —
    /// when it would change one. The declaration is read once per activation and kept for the
    /// hub's life, the same binding the rest of the hub's configuration gets. A type that declares
    /// no field costs one cached lookup per client write.
    /// </summary>
    /// <param name="config">The node hub's configuration.</param>
    /// <returns>The configuration with the guard installed.</returns>
    public static MessageHubConfiguration AddStampedFieldGuard(this MessageHubConfiguration config)
        => config.AddDeliveryPipeline(pipeline =>
        {
            var hub = pipeline.Hub;
            // Instance state of THIS hub's pipeline, built once per hub: the declaration is bound for
            // the activation's life (a fault is evicted, so the next write reads again).
            var declaration = new PromiseSlot<StampedFieldDeclaration>();
            ILogger? logger = null;

            return pipeline.AddPipeline((delivery, ct, next) =>
            {
                if (!IsClientWriteToThisHub(hub, delivery))
                    return next.Invoke(delivery, ct);

                logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Graph.StampedFields");

                var verdicts = declaration
                    .GetOrCreate(() => Resolve(hub, OwnNodeType(hub)).Replay(1).AutoConnect(1))
                    .Take(1)
                    .Select(declared => VerdictFor(hub, delivery, declared));

                // A settled declaration answers synchronously, and the delivery then continues on
                // THIS call exactly as if the step were not here — only the first write of an
                // activation, while the declaration is being read, takes the deferred shape (the
                // same shape the access gate uses for every check it runs).
                const int Pending = 0, Answered = 1, Deferred = 2;
                var state = Pending;
                string? syncRefusal = null;
                Exception? syncFault = null;
                verdicts.Subscribe(
                    refusal =>
                    {
                        if (Interlocked.CompareExchange(ref state, Answered, Pending) == Pending)
                        {
                            syncRefusal = refusal;
                            return;
                        }
                        if (refusal is null)
                            next.Invoke(delivery, ct).Subscribe(
                                _ => { },
                                ex => logger?.LogError(ex,
                                    "StampedFields: downstream pipeline faulted for {MessageType} on {Hub}",
                                    delivery.Message.GetType().Name, hub.Address));
                        else
                            Refuse(hub, delivery, ErrorType.Unauthorized, refusal, logger);
                    },
                    ex =>
                    {
                        if (Interlocked.CompareExchange(ref state, Answered, Pending) == Pending)
                        {
                            syncFault = ex;
                            return;
                        }
                        Refuse(hub, delivery, ErrorType.Unavailable, Unavailable(hub, ex), logger);
                    });

                if (Interlocked.CompareExchange(ref state, Deferred, Pending) == Pending)
                    return Observable.Return(delivery.Forwarded());

                if (syncFault is not null)
                {
                    Refuse(hub, delivery, ErrorType.Unavailable, Unavailable(hub, syncFault), logger);
                    return Observable.Return(delivery.Forwarded());
                }
                if (syncRefusal is not null)
                {
                    Refuse(hub, delivery, ErrorType.Unauthorized, syncRefusal, logger);
                    return Observable.Return(delivery.Forwarded());
                }
                return next.Invoke(delivery, ct);
            });
        });

    /// <summary>
    /// A write ADDRESSED to this hub's own node that came from anywhere but this hub. The owning hub's
    /// own writes never arrive as a message (they commit on its own data source), so the sender test
    /// is exact; a write merely passing through to a hosted hub is not this hub's to judge.
    /// </summary>
    private static bool IsClientWriteToThisHub(IMessageHub hub, IMessageDelivery delivery)
    {
        if (delivery.Message is not (PatchDataRequest { Reference: MeshNodeReference } or DataChangeRequest))
            return false;
        var ownPath = hub.Address.Path;
        if (delivery.Target is { } target && !string.Equals(target.Path, ownPath, StringComparison.OrdinalIgnoreCase))
            return false;
        return delivery.Sender is null
            || !string.Equals(delivery.Sender.Path, ownPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The NodeType whose configuration this hub runs — the type that declares the fields.</summary>
    private static string? OwnNodeType(IMessageHub hub)
        => hub.Configuration.Get<NodeTypePathHolder>()?.Path ?? LiveOwnNode(hub)?.NodeType;

    /// <summary>The hub's own node as its data source holds it now (null before it has loaded).</summary>
    private static MeshNode? LiveOwnNode(IMessageHub hub)
    {
        var store = hub.GetWorkspace().DataContext.GetDataSourceForType(typeof(MeshNode))
            ?.GetStreamForPartition(null)?.Current?.Value;
        var ownPath = hub.Address.Path;
        return store?.Collections.GetValueOrDefault(nameof(MeshNode))?.Instances.Values
            .OfType<MeshNode>()
            .FirstOrDefault(n => string.Equals(n.Path, ownPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The refusal for <paramref name="delivery"/>, or null when it changes no stamped field.</summary>
    private static string? VerdictFor(IMessageHub hub, IMessageDelivery delivery, StampedFieldDeclaration declared)
    {
        if (declared.Fields.IsEmpty)
            return null;

        var options = hub.JsonSerializerOptions;
        var live = LiveOwnNode(hub);
        var liveContent = ContentObject(live, options);
        var path = live?.Path ?? hub.Address.Path;
        var nodeType = live?.NodeType ?? OwnNodeType(hub);

        switch (delivery.Message)
        {
            case PatchDataRequest patchRequest:
            {
                if (JsonNode.Parse(patchRequest.Patch.Content ?? "{}") is not JsonObject patch)
                    return null; // not an object: the handler refuses it with its own error
                var contentKey = options.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.Content))
                    ?? nameof(MeshNode.Content);
                var changed = ChangedByMergePatch(liveContent, patch, contentKey, declared.Fields);
                return changed.IsEmpty ? null : Refusal(path, nodeType, changed, isCreate: false);
            }
            case DataChangeRequest change:
            {
                var changed = ImmutableList<string>.Empty;
                foreach (var proposed in change.Updates.Concat(change.Creations))
                {
                    var after = proposed switch
                    {
                        MeshNode node when IsOwn(node.Path, path) => node,
                        EntityDeltaUpdate delta when live is not null
                            && delta.Collection == nameof(MeshNode)
                            && (delta.Id == live.Id || string.Equals(delta.Id, live.Path, StringComparison.OrdinalIgnoreCase))
                            => EntityDelta.Apply(live, delta, options) as MeshNode,
                        _ => null
                    };
                    if (after is null)
                        continue;
                    changed = changed.AddRange(
                        Changed(liveContent, ContentObject(after, options), declared.Fields)
                            .Where(f => !changed.Contains(f)));
                }
                return changed.IsEmpty ? null : Refusal(path, nodeType, changed, isCreate: false);
            }
            default:
                return null;
        }
    }

    private static bool IsOwn(string? candidate, string ownPath)
        => string.Equals(candidate, ownPath, StringComparison.OrdinalIgnoreCase);

    private static string Unavailable(IMessageHub hub, Exception ex)
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
