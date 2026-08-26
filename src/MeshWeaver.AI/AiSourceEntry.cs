using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>The three kinds of AI source a user resolves through their settings.</summary>
public static class AiSourceKinds
{
    /// <summary>Skill sources (<c>nodeType:Skill</c>).</summary>
    public const string Skill = "skill";
    /// <summary>Agent sources (<c>nodeType:Agent</c>).</summary>
    public const string Agent = "agent";
    /// <summary>Language-model sources (<c>nodeType:LanguageModel|ModelProvider|ModelTier</c>).</summary>
    public const string Model = "model";

    /// <summary>Every kind, in display order.</summary>
    public static readonly ImmutableArray<string> All = ImmutableArray.Create(Skill, Agent, Model);

    /// <summary>True for a known kind (case-insensitive).</summary>
    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical (lower-case) spelling of a known kind, or null.</summary>
    public static string? Canonical(string? kind) =>
        All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One NAMED, DESCRIBED resolution source in a user's <see cref="AiSettings"/> — "where do my
/// skills / agents / models come from, and why". The <see cref="Query"/> is a TEMPLATE carrying
/// placeholders (<see cref="AiSourceCatalog"/> lists them: <c>{user}</c>, <c>{objectPartition}</c>,
/// <c>{objectPath}</c>, <c>{nodeTypePartition}</c>, <c>{nodeTypePath}</c>, plus the legacy
/// <c>{userPath}</c> / <c>{currentPath}</c> / <c>{nodeTypePath}</c> spellings) that the seam expands
/// per context. A template whose placeholder has no value in the current context is DROPPED — never
/// emitted half-expanded — and a template that is not partition-anchored is never executed.
/// </summary>
public record AiSourceEntry
{
    /// <summary>One of <see cref="AiSourceKinds"/>.</summary>
    public string Kind { get; init; } = AiSourceKinds.Skill;

    /// <summary>The label a user sees — "My skills", "Skills of the current space", "MeshWeaver OpenRouter".</summary>
    public string Name { get; init; } = "";

    /// <summary>What this source finds and why it is there — rendered beside the name everywhere the
    /// entry shows.</summary>
    public string? Description { get; init; }

    /// <summary>The query TEMPLATE (placeholders unexpanded). Must be partition-anchored
    /// (<c>namespace:</c> or <c>path:</c>) once expanded.</summary>
    public string Query { get; init; } = "";
}

/// <summary>
/// The context a source template is expanded against: the viewer's home partition, the node in
/// view (full path + its partition) and that node's TYPE (full path + its partition). Built through
/// <see cref="AiSourceCatalog.Context"/>, which nulls reserved/rogue route partitions so a poisoned
/// context can never fail a query.
/// </summary>
public sealed record AiSourceContext
{
    /// <summary>The viewer's home partition (their user id), or null when signed out.</summary>
    public string? User { get; init; }
    /// <summary>The full path of the node in view, or null.</summary>
    public string? ObjectPath { get; init; }
    /// <summary>The top-level partition of the node in view, or null.</summary>
    public string? ObjectPartition { get; init; }
    /// <summary>The full path of the node type of the node in view, or null.</summary>
    public string? NodeTypePath { get; init; }
    /// <summary>The top-level partition of that node type, or null.</summary>
    public string? NodeTypePartition { get; init; }
}

/// <summary>One entry after expansion: the query the platform will run for it, or why it was dropped.</summary>
public sealed record AiResolvedSource(
    AiSourceEntry Entry,
    string? Query,
    bool IsDefault,
    string? DroppedReason)
{
    /// <summary>True when the entry produced a runnable query.</summary>
    public bool IsActive => Query is not null;
}
