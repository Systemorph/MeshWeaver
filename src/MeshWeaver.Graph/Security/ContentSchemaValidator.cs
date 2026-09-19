using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Fail-closed content-integrity guard for Create/Update: refuses a write whose
/// <see cref="MeshNode.Content"/> CANNOT BIND to the content type its NodeType declares — naming
/// the member and the type that member was declared as.
///
/// <para><b>The defect this kills</b> (Systemorph/MeshWeaver#4601, measured 2026-09-17 on
/// memex.systemorph.com). A <c>patch</c> that put a JSON OBJECT into a member declared
/// <c>public string?</c> was accepted, versioned and stored VERBATIM. Nothing errored; the record
/// simply stopped deserialising as its declared type, so every reader's
/// <c>ContentAs&lt;T&gt;</c> answered <c>null</c> from then on. That is the trigger of the
/// silent-empty class: the page renders the "no content yet" placeholder over a full document, a
/// reactive wait for the typed shape never completes, and nothing is logged as an error
/// (#4600 is the read-side half, #3623 the class). The same silence accepted a <c>Markdown</c>
/// CREATE whose content carried the text under a member named <c>markdown</c> — a member
/// <see cref="Markdown.MarkdownContent"/> does not declare — so the node rendered empty from v1.</para>
///
/// <para><b>Why the write boundary.</b> Stored content that no reader can materialise is DURABLE
/// corruption discovered months later by whoever opens the page; the caller who could have fixed it
/// in one retry is long gone. The read seams are already as tolerant as they can be
/// (<see cref="IMeshContentTypeRegistry.TryRecoverForNodeType"/>, <c>ContentAs&lt;T&gt;</c>), and
/// tolerance is the right posture there — it is the wrong posture for a NEW write.</para>
///
/// <list type="number">
///   <item><b>A member whose value contradicts its declared type</b> — an object into a
///     <c>string?</c>, a string into an <c>int</c>. Refused, naming the member and the declared
///     type. This is the ONE shape refused on a bind failure: the judgement is scoped to
///     <see cref="JsonException.Path"/> naming a MEMBER (<c>$.country</c>), never a whole-document
///     failure (<c>$</c>) — a missing <c>required</c> member is the ordinary partial-content shape
///     a legitimate writer produces, and refusing it would turn this guard into a schema-strictness
///     change nobody asked for. 🚨 "Not judged on the bind" means the whole-document failure falls
///     THROUGH to rule 2, not that the exception leaves this method: a <c>when</c> filter on the
///     catch let it escape and fail the write with the raw serializer text, which is the opposite
///     of saying nothing (#4648).</item>
///   <item><b>Content NONE of whose members the declared type knows</b> — the
///     <c>{"markdown": "…"}</c>-on-a-Markdown-node shape. System.Text.Json's
///     <see cref="JsonUnmappedMemberHandling.Skip"/> makes this bind CLEANLY to an instance
///     carrying none of the authored data, which is why no exception can catch it. Refused only in
///     the total case — at least one member present and NOT ONE of them declared — so content
///     carrying an extra member alongside real ones (an older or newer writer, a legacy field)
///     still lands, and the read path's <c>WarnIfLossy</c> keeps reporting what it drops. This is
///     also where a whole-document bind failure lands, so a declared type with a <c>required</c>
///     member — whose bind throws before <c>Skip</c> can apply — is judged by the same rule as one
///     without.</item>
/// </list>
///
/// <para><b>Scope, and why it is not the discriminator guard's.</b>
/// <see cref="ContentDiscriminatorValidator"/> asks whether the content's OWN <c>$type</c> resolves
/// on the VALIDATING hub's registry, which is definitive only for framework built-ins — an in-mesh
/// compiled type's content types live on its own per-node hub, so it exempts them, and
/// <c>Crm/Client</c> (the type #4601 was measured on) is exactly such a type. This guard asks a
/// different question of a different instrument: <see cref="IMeshContentTypeRegistry"/> is the
/// MESH-WIDE, options-independent map keyed on the node's own <see cref="MeshNode.NodeType"/> —
/// unique by construction, so it is never the ambiguous-discriminator answer — populated by
/// <c>MeshDataSource.WithContentType</c> for an in-mesh type at its first instance activation and
/// by <c>ContentTypeRegistrationSweep</c> for every static definition at boot.</para>
///
/// <para>🚨 <b>Where it deliberately says nothing.</b> No entry for the NodeType ⇒ Valid: a type
/// nothing has declared a content type for legitimately stores free-form JSON, and an in-mesh type
/// whose first instance has not activated yet must not have its writes refused for a fact this
/// process has not learned. Typed (in-process) CLR content ⇒ Valid: it already bound. Content whose
/// own <c>$type</c> names a DIFFERENT record than the declared one ⇒ Valid, the same
/// <see cref="ContentDiscriminator.Admits(JsonElement, Type)"/> rule the recovery path applies —
/// judging such content by the declared
/// type would be reshaping it, and the discriminator guard owns that case. An Update whose content
/// is byte-identical to what is stored ⇒ Valid: re-asserting a row already on disk is not a new
/// write of bad content, and refusing it would make an existing broken node impossible to move.</para>
/// </summary>
public sealed class ContentSchemaValidator : INodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<ContentSchemaValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the content-schema validator.
    /// </summary>
    /// <param name="hub">The message hub providing the mesh-wide content-type map and options.</param>
    /// <param name="logger">The logger used to record blocked writes.</param>
    public ContentSchemaValidator(IMessageHub hub, ILogger<ContentSchemaValidator> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Create + Update — the two surfaces that persist content.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    /// <summary>
    /// Validates a create or update, refusing content that cannot bind to the content type the
    /// node's NodeType declares.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable that emits the validation result for the operation.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(Judge(context));

    private NodeValidationResult Judge(NodeValidationContext context)
    {
        var node = context.Node;

        // Typed (in-process) content has already bound; null content and non-object JSON carry no
        // members to judge.
        if (node.Content is not JsonElement content || content.ValueKind != JsonValueKind.Object)
            return NodeValidationResult.Valid();

        if (string.IsNullOrEmpty(node.NodeType))
            return NodeValidationResult.Valid();

        // The NodeType→content-type map, not this hub's $type registry: see the class doc.
        var contentTypes = _hub.ServiceProvider.GetService<IMeshContentTypeRegistry>();
        if (contentTypes is null || !contentTypes.TryResolveByNodeType(node.NodeType!, out var declared))
            return NodeValidationResult.Valid();

        if (!ContentDiscriminator.Admits(content, declared))
            return NodeValidationResult.Valid();

        // Re-asserting bytes already on disk is not a new write of bad content.
        if (context.Operation == NodeOperation.Update && IsUnchanged(context.ExistingNode, content))
            return NodeValidationResult.Valid();

        var options = _hub.JsonSerializerOptions;
        try
        {
            // Deserialise from the ELEMENT, not from GetRawText(): the raw text would allocate the
            // whole payload as a string and reparse it on every judged write, and every consumer
            // that binds a JsonElement here (MeshContentTypeRegistry.Materialize, ContentAs<T>)
            // already reads it directly (review on #4624).
            content.Deserialize(declared, options);
        }
        catch (JsonException ex)
        {
            // 🚨 The catch is UNCONDITIONAL and the judgement happens INSIDE it. A `when` filter
            // here (the original shape) left a whole-document failure — `$`, which is what a
            // missing `required` member raises — UNCAUGHT, so the JsonException escaped the
            // validator and failed the write it documents as NOT JUDGED. The agent surface then
            // reported the raw serializer text ("… was missing required properties including:
            // 'content'") as the reason its update failed, in English, for a write this guard has
            // no opinion about (Systemorph/MeshWeaver#4648: two MeshPluginTest cases red in core's
            // CD, on a `Markdown` node whose content omitted `MarkdownContent.Content`).
            if (MemberOf(ex.Path) is { } member)
                return Refuse(
                    context,
                    LocalizationCatalog.Get(
                        "content.schema.memberTypeMismatch", context.AccessContext?.Locale,
                        node.Path, member, DeclaredTypeName(declared, member, options),
                        ValueKindOf(content, member), declared.Name, node.NodeType!),
                    $"member '{member}' does not match its declared type");

            // A whole-document failure is not judged on the bind — a missing `required` member is
            // the ordinary partial-content shape a legitimate writer produces. Fall THROUGH to the
            // member-counting rule below rather than returning Valid here: that rule is the half
            // that catches the `{"markdown":"…"}`-on-a-Markdown-node shape, and a content type with
            // a `required` member is exactly the case where the bind throws before it ever runs.
            _logger.LogDebug(ex,
                "ContentSchemaGuard: content of {Path} does not bind to {ContentType} as a whole "
                + "(no member blamed) — not judged on the bind; the declared-member rule decides.",
                node.Path, declared.Name);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // The target has no usable contract here (a collectible assembly gone, a type the
            // resolver refuses). That is a fact about THIS process, not about the content — say
            // nothing rather than refuse a write on it.
            _logger.LogDebug(ex,
                "ContentSchemaGuard: no contract for content type {ContentType} of NodeType "
                + "{NodeType} on this hub — {Path} not judged.",
                declared.Name, node.NodeType, node.Path);
            return NodeValidationResult.Valid();
        }

        // It bound (or failed as a whole, above) — but UnmappedMemberHandling.Skip means "bound"
        // can mean "carried nothing", and a type with a `required` member never gets that far.
        if (HasExtensionDataBuffer(declared, options))
            return NodeValidationResult.Valid();
        var names = DeclaredMemberNames(declared, options);
        if (names is null)
            return NodeValidationResult.Valid();
        var authored = content.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !IsDiscriminator(n))
            .ToImmutableList();
        var unknown = authored.Where(n => !names.Contains(n)).ToImmutableList();
        if (unknown.Count == 0 || unknown.Count != authored.Count)
            return NodeValidationResult.Valid();

        return Refuse(
            context,
            LocalizationCatalog.Get(
                "content.schema.noDeclaredMember", context.AccessContext?.Locale,
                node.Path, string.Join("', '", unknown), declared.Name, node.NodeType!,
                string.Join("', '", names.Order(StringComparer.Ordinal))),
            $"no member of the content is declared by '{declared.Name}'");
    }

    /// <summary>Logs the refusal (with the reason a reader of the portal log needs) and returns it.</summary>
    private NodeValidationResult Refuse(NodeValidationContext context, string message, string summary)
    {
        _logger.LogWarning(
            "ContentSchemaGuard: blocked {Operation} of '{Path}' (NodeType '{NodeType}') — {Summary}. "
            + "Content that cannot bind to its declared type is stored verbatim and then reads as "
            + "absent on every consumer: the page renders empty over a full document and nothing "
            + "errors. Systemorph/MeshWeaver#4601.",
            context.Operation, context.Node.Path, context.Node.NodeType, summary);
        return NodeValidationResult.Invalid(message);
    }

    /// <summary>
    /// The top-level member a <see cref="JsonException.Path"/> blames, or null when the failure is
    /// about the document as a whole (<c>$</c>) — see the class doc for why that half is not judged.
    /// </summary>
    private static string? MemberOf(string? jsonPath)
    {
        if (jsonPath is null || !jsonPath.StartsWith("$.", StringComparison.Ordinal))
            return null;
        var rest = jsonPath[2..];
        var cut = rest.IndexOfAny(['.', '[']);
        var member = cut < 0 ? rest : rest[..cut];
        return member.Length == 0 ? null : member;
    }

    /// <summary>The declared CLR type of <paramref name="member"/>, for the refusal message.</summary>
    private static string DeclaredTypeName(Type declared, string member, JsonSerializerOptions options)
    {
        try
        {
            var property = options.GetTypeInfo(declared).Properties
                .FirstOrDefault(p => string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase));
            return property is null ? declared.Name : FriendlyName(property.PropertyType);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return declared.Name;
        }
    }

    /// <summary>The C# spelling of a member type, so the message reads as the declaration does.</summary>
    private static string FriendlyName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return FriendlyName(underlying) + "?";
        return type == typeof(string) ? "string"
            : type == typeof(int) ? "int"
            : type == typeof(long) ? "long"
            : type == typeof(double) ? "double"
            : type == typeof(decimal) ? "decimal"
            : type == typeof(bool) ? "bool"
            : type.Name;
    }

    /// <summary>What the content actually put there, in JSON's own vocabulary.</summary>
    private static string ValueKindOf(JsonElement content, string member)
        => content.TryGetProperty(member, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => "value",
            }
            : "value";

    private static bool IsDiscriminator(string name)
        => string.Equals(name, "$type", StringComparison.Ordinal);

    private static ImmutableHashSet<string>? DeclaredMemberNames(Type declared, JsonSerializerOptions options)
    {
        try
        {
            return options.GetTypeInfo(declared).Properties
                .Select(p => p.Name)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool HasExtensionDataBuffer(Type declared, JsonSerializerOptions options)
    {
        try
        {
            return options.GetTypeInfo(declared).Properties.Any(p => p.IsExtensionData);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return true;   // unknown ⇒ judge nothing
        }
    }

    /// <summary>
    /// Whether the proposed content is the bytes the node already holds. Compared as RAW JSON,
    /// because the existing side may itself be a degraded <see cref="JsonElement"/> — the ordinary
    /// shape for a content type only the owning hub registers.
    /// </summary>
    private bool IsUnchanged(MeshNode? existing, JsonElement proposed)
    {
        if (existing?.Content is null)
            return false;
        try
        {
            var existingJson = existing.Content is JsonElement je
                ? je.GetRawText()
                : JsonSerializer.Serialize(existing.Content, _hub.JsonSerializerOptions);
            return string.Equals(existingJson, proposed.GetRawText(), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
