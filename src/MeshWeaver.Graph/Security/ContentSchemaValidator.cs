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
///     change nobody asked for. 🚨 Not refused is not the same as not CAUGHT: a whole-document
///     failure is caught and carried into rule 2 below, because an exception thrown out of a
///     validator fails the write just as hard as a refusal, and less legibly — that escape is what
///     made every <c>Markdown</c> write omitting <c>content</c> fail with a raw System.Text.Json
///     message on 2026-09-17.</item>
///   <item><b>Content NONE of whose members the declared type knows</b> — the
///     <c>{"markdown": "…"}</c>-on-a-Markdown-node shape. System.Text.Json's
///     <see cref="JsonUnmappedMemberHandling.Skip"/> makes this bind CLEANLY to an instance
///     carrying none of the authored data, which is why no exception can catch it — and a type with
///     a <c>required</c> member reaches the same census from the other direction, having thrown
///     rather than bound. Refused only in
///     the total case — at least one member present and NOT ONE of them declared — so content
///     carrying an extra member alongside real ones (an older or newer writer, a legacy field)
///     still lands, and the read path's <c>WarnIfLossy</c> keeps reporting what it drops.</item>
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
/// <c>DiscriminatorAdmits</c> rule the recovery path applies — judging such content by the declared
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

        if (!DiscriminatorAdmits(content, declared))
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
        catch (JsonException ex) when (MemberOf(ex.Path) is { } member)
        {
            return Refuse(
                context,
                LocalizationCatalog.Get(
                    "content.schema.memberTypeMismatch", context.AccessContext?.Locale,
                    node.Path, member, DeclaredTypeName(declared, member, options),
                    ValueKindOf(content, member), declared.Name, node.NodeType!),
                $"member '{member}' does not match its declared type");
        }
        catch (JsonException ex)
        {
            // 🚨 A WHOLE-DOCUMENT failure — `Path` is `$` or absent, which in practice means a
            // `required` member the payload does not carry. It is NOT a member contradiction, and
            // the class doc above says why it must not be refused: partial content is what a
            // legitimate writer produces.
            //
            // It must not ESCAPE either, and that is the defect this clause closes. With only the
            // filtered clause above, a JsonException whose Path names no member matched NO catch
            // at all, so it left this validator and failed the write with a raw System.Text.Json
            // message — the guard refusing, loudly and untranslated, the one shape it had promised
            // to admit. Measured 2026-09-17: every `Markdown` write whose content omitted
            // `content` began failing with "was missing required properties including: 'content'",
            // which took MeshWeaver.Plugins main dark (Plugins#2049) over a payload this guard
            // exempts twice over — once as partial content, once for the extension-data buffer
            // MarkdownContent carries.
            //
            // Fall through to the member census below: it is the one judgement that still applies
            // to content which did not bind, and it answers Valid for a type with an extension-data
            // buffer and for any payload naming at least one declared member, so the only thing it
            // can still refuse is content NONE of whose members the type knows — the #4601 shape,
            // now refused by the guard's own localized message instead of by an escaping exception.
            _logger.LogDebug(ex,
                "ContentSchemaGuard: content for {Path} does not bind to {ContentType} as a whole "
                + "({JsonPath}) — partial content is not refused; judging its members instead.",
                node.Path, declared.Name, ex.Path ?? "$");
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

        // It bound — but UnmappedMemberHandling.Skip means "bound" can mean "carried nothing" —
        // or it did not bind as a whole document, which says nothing about its members either
        // way. Both reach here, and this census is what separates partial content from content
        // the declared type shares no member with.
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
    /// Whether the content's own <c>$type</c> does not CONTRADICT the declared content type — the
    /// same short-name rule <c>MeshContentTypeRegistry.DiscriminatorAdmits</c> applies, so two
    /// packages that each ship a <c>Currency</c> both pass through their own NodeType's entry, and
    /// a rebuild into a new collectible assembly matches. Absent is not contradicting.
    /// </summary>
    private static bool DiscriminatorAdmits(JsonElement content, Type declared)
    {
        if (!content.TryGetProperty("$type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String
            || typeProp.GetString() is not { Length: > 0 } discriminator)
            return true;
        var lastDot = discriminator.LastIndexOf('.');
        var shortName = lastDot >= 0 ? discriminator[(lastDot + 1)..] : discriminator;
        return string.Equals(shortName, declared.Name, StringComparison.Ordinal);
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
