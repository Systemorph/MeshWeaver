using System.Collections.Immutable;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// The node types an app CATALOGS — the types whose instances the app is the place to browse
/// (the CRM app catalogs its clients, the Inbox its mail, the Threads app its threads). A type an
/// installed app catalogs is never given a section of its own on the home below the Apps band: the
/// app IS its catalog (Doc/Architecture/AppsHome → "Type sections").
/// <para>Value-equal (set semantics, ordinal and case-insensitive), so a re-read of an unchanged
/// declaration compares equal and the directory's change detection stays quiet.</para>
/// </summary>
public sealed record CatalogedTypeSet
{
    /// <summary>The empty set — what every app that catalogs nothing declares.</summary>
    public static readonly CatalogedTypeSet Empty = new();

    /// <summary>The node types (their NodeType paths, e.g. <c>Crm/Client</c>).</summary>
    public ImmutableSortedSet<string> Types { get; init; } =
        ImmutableSortedSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A set of <paramref name="types"/>; blanks are dropped and entries trimmed. Pure.</summary>
    public static CatalogedTypeSet Of(params IEnumerable<string?> types) =>
        new()
        {
            Types = types
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim().Trim('/'))
                .Where(t => t.Length > 0)
                .ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>Whether the set holds no type.</summary>
    public bool IsEmpty => Types.Count == 0;

    /// <summary>Whether <paramref name="nodeType"/> is catalogued. Pure.</summary>
    public bool Contains(string? nodeType) => nodeType is { Length: > 0 } t && Types.Contains(t);

    /// <summary>The union of this set and <paramref name="other"/>. Pure.</summary>
    public CatalogedTypeSet Union(CatalogedTypeSet other) =>
        other.IsEmpty ? this : IsEmpty ? other : new() { Types = Types.Union(other.Types) };

    /// <summary>Set equality.</summary>
    public bool Equals(CatalogedTypeSet? other) =>
        other is not null && (ReferenceEquals(this, other) || Types.SetEquals(other.Types));

    /// <inheritdoc />
    public override int GetHashCode() => Types.Count;
}
