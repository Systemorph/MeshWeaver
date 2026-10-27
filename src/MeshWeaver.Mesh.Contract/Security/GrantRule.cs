using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A DECLARED, TYPE-SCOPED grant: "every partition whose ROOT node is of
/// <see cref="NodeType"/> (and, optionally, whose root content carries
/// <see cref="ContentField"/> = <see cref="ContentValue"/>) grants <see cref="AccessObject"/> the
/// role <see cref="Role"/>".
///
/// <para><b>Computed, never materialised.</b> The permission fold (<c>PermissionEvaluator</c>) and
/// the Postgres projection (<c>rebuild_user_effective_permissions</c>, MeshWeaver.Plugins) both
/// derive the grant from the rule and the partition root on every evaluation — no
/// <c>AccessAssignment</c> row is ever written for it. A partition created with a matching root is
/// covered the moment it exists; a root retyped away from the match drops out the moment the
/// retype lands. There is no sweep, no reconciler, and nothing to drift.</para>
///
/// <para><b>Instance DATA, not module code.</b> A rule lives at
/// <c>Admin/_GrantRule/{id}</c> (<see cref="Namespace"/>) — the one partition only a global admin
/// can write — because the node type a rule names usually ships in a package installed on many
/// instances while the subject it grants is particular to ONE instance. That is the difference to
/// <see cref="NodeTypeGate"/>, which a TYPE declares about itself and which only ever opens Read.</para>
///
/// <para><b>It is a broad grant</b> (one declaration reaches every matching partition), so a
/// create or update of a rule is written only by a governed activity executing
/// <c>access.grant-rule</c> (<see cref="Services.BroadGrantGuard"/>,
/// <see cref="Services.BroadGrantKind.GrantRule"/>). Deleting one only removes access.</para>
///
/// <para><b>A rule-derived role is a role like any other</b>: it sits at the partition ROOT's scope,
/// subject expansion (groups, transitively) applies, a <c>Denied</c> assignment at the root or
/// deeper subtracts it, a <c>_Policy</c> cap or inheritance break applies to it, and the
/// system-owned (one-way GitSync) write cap removes Create/Update/Delete from it.</para>
/// </summary>
public record GrantRule
{
    /// <summary>The node type of a grant rule.</summary>
    public const string NodeTypeName = "GrantRule";

    /// <summary>Where grant rules live: the Admin partition, out of reach of everyone but a global admin.</summary>
    public const string Namespace = "Admin/_GrantRule";

    /// <summary>The path of the rule <paramref name="id"/>.</summary>
    /// <param name="id">The rule id.</param>
    /// <returns><c>Admin/_GrantRule/{id}</c>.</returns>
    public static string PathOf(string id) => $"{Namespace}/{id}";

    /// <summary>
    /// The node type a partition ROOT must carry for the rule to apply (e.g.
    /// <c>Crm/Counterparty</c>). Matched exactly (ordinal), the same comparison the SQL projection
    /// makes.
    /// </summary>
    public string NodeType { get; init; } = "";

    /// <summary>
    /// Optional: a TOP-LEVEL field of the root node's content that must equal
    /// <see cref="ContentValue"/> (e.g. <c>type</c>). Null or empty = every root of
    /// <see cref="NodeType"/> matches.
    /// </summary>
    public string? ContentField { get; init; }

    /// <summary>
    /// The value <see cref="ContentField"/> must hold, compared ordinally against the field's text.
    ///
    /// <para>🚨 <b>Only a JSON STRING or BOOLEAN field can match</b> — a string as written,
    /// <c>true</c>/<c>false</c> for a boolean. Those are the kinds whose text is identical in both
    /// evaluators (this fold and Postgres' <c>content->>'field'</c>). A number, array or object
    /// never matches: <c>jsonb</c> re-renders them canonically (<c>[1, 2]</c>, <c>1E+20</c> as
    /// digits) while the stored JSON text is the writer's, so an ordinal comparison could grant in
    /// one evaluator and deny in the other. The SQL projection restricts to the same two kinds
    /// (<c>jsonb_typeof</c>). A missing or null field never matches.</para>
    /// </summary>
    public string? ContentValue { get; init; }

    /// <summary>The subject — a user id or a group path. <c>Public</c> and <c>Anonymous</c> are never honoured.</summary>
    [MeshNode(AccessSubjectQueries.Users, AccessSubjectQueries.GroupsTemplate)]
    public string AccessObject { get; init; } = "";

    /// <summary>The role granted at each matching partition root (e.g. <c>Editor</c>, <c>Admin</c>).</summary>
    public string Role { get; init; } = "";

    /// <summary>Why the rule exists — shown to whoever reviews the Admin partition.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The governed activity that wrote this rule — the back-reference the governance executor
    /// writes and <see cref="Services.BroadGrantGuard"/> checks against the write's context.
    /// </summary>
    [Browsable(false)]
    public string? GovernedBy { get; init; }

    /// <summary>
    /// Whether the rule can grant anything at all: a type, a named (non-broad) subject and a role,
    /// and a content filter that is either absent or complete. An invalid rule grants nothing.
    /// </summary>
    public bool IsEffective =>
        !string.IsNullOrWhiteSpace(NodeType)
        && !string.IsNullOrWhiteSpace(AccessObject)
        && !string.IsNullOrWhiteSpace(Role)
        && !Services.BroadGrantGuard.IsBroadSubject(AccessObject.Trim())
        && (string.IsNullOrEmpty(ContentField) || ContentValue is not null);
}

/// <summary>
/// The pure half of <see cref="GrantRule"/> evaluation — reading rules from nodes and matching them
/// against a partition root. No I/O; the fold supplies the nodes.
/// </summary>
public static class GrantRuleEvaluator
{
    /// <summary>
    /// The effective rules among <paramref name="nodes"/> — typed or raw content, only nodes that sit
    /// directly in <see cref="GrantRule.Namespace"/> (a rule node placed anywhere else is inert).
    /// </summary>
    /// <param name="nodes">The candidate nodes.</param>
    /// <param name="options">Serializer options for raw content.</param>
    /// <returns>The effective rules, ordered by path.</returns>
    public static ImmutableArray<GrantRule> ReadRules(IEnumerable<MeshNode> nodes, JsonSerializerOptions options)
    {
        var builder = ImmutableArray.CreateBuilder<(string Path, GrantRule Rule)>();
        foreach (var node in nodes)
        {
            if (!string.Equals(node.NodeType, GrantRule.NodeTypeName, StringComparison.Ordinal)
                || !string.Equals(node.Namespace, GrantRule.Namespace, StringComparison.Ordinal))
                continue;
            var rule = Deserialize(node.Content, options);
            if (rule is { IsEffective: true })
                builder.Add((node.Path ?? "", rule));
        }
        return builder.OrderBy(x => x.Path, StringComparer.Ordinal).Select(x => x.Rule).ToImmutableArray();
    }

    /// <summary>
    /// The rules that apply to the partition whose root is <paramref name="root"/>: its node type
    /// equals the rule's, and the content filter (when stated) holds. A missing root matches nothing,
    /// and so does a node that is not a partition root (its path holds a <c>/</c>).
    /// </summary>
    /// <param name="rules">The effective rules.</param>
    /// <param name="root">The partition root node, or null when it does not exist.</param>
    /// <param name="options">Serializer options for raw content.</param>
    /// <returns>The matching rules.</returns>
    public static ImmutableArray<GrantRule> Matching(
        ImmutableArray<GrantRule> rules, MeshNode? root, JsonSerializerOptions options)
    {
        if (rules.IsDefaultOrEmpty || root is null || string.IsNullOrEmpty(root.Path)
            || root.Path.Contains('/') || string.IsNullOrEmpty(root.NodeType))
            return ImmutableArray<GrantRule>.Empty;

        JsonElement? content = null;
        var result = ImmutableArray.CreateBuilder<GrantRule>();
        foreach (var rule in rules)
        {
            if (!string.Equals(rule.NodeType, root.NodeType, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrEmpty(rule.ContentField))
            {
                content ??= AsElement(root.Content, options);
                if (FieldText(content.Value, rule.ContentField) is not { } text
                    || !string.Equals(text, rule.ContentValue, StringComparison.Ordinal))
                    continue;
            }
            result.Add(rule);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// The text of a top-level content field, for the kinds whose text both evaluators agree on
    /// (<see cref="GrantRule.ContentValue"/>): a string's value, or <c>true</c>/<c>false</c> — exactly
    /// what Postgres' <c>-&gt;&gt;</c> yields for them. Null — never matches — for a missing field,
    /// JSON null, and a number, array or object, whose <c>jsonb</c> text is a canonical
    /// re-rendering that need not equal the stored JSON text.
    /// </summary>
    private static string? FieldText(JsonElement content, string field)
    {
        if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty(field, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static JsonElement AsElement(object? content, JsonSerializerOptions options)
    {
        try
        {
            return content switch
            {
                null => default,
                JsonElement je => je,
                JsonNode jn => JsonSerializer.SerializeToElement(jn, options),
                _ => JsonSerializer.SerializeToElement(content, content.GetType(), options),
            };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return default;
        }
    }

    // The framework's one shape-tolerant reader: typed, both JSON shapes, and a same-named type
    // from another assembly (a recoverable rule must never read as absent).
    private static GrantRule? Deserialize(object? content, JsonSerializerOptions options)
        => content.As<GrantRule>(options, what: GrantRule.Namespace);
}
