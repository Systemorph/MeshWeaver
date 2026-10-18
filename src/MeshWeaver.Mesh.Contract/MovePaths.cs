namespace MeshWeaver.Mesh;

/// <summary>
/// How a move's target relates to its source — the vocabulary <see cref="MovePaths.Classify"/>
/// answers in. Not persisted and not serialised; string constants by house rule.
/// </summary>
internal static class MovePathRelation
{
    /// <summary>The target is the source, byte for byte: the move would move nothing.</summary>
    public const string Same = nameof(Same);

    /// <summary>
    /// The target names the same node as the source in every segment but differs in case — a
    /// rename of the casing only (<c>VhvGruppe → VHVGruppe</c>). The mesh keys paths
    /// case-insensitively, so the two are one key in a case-insensitive store.
    /// </summary>
    public const string CaseOnlyRename = nameof(CaseOnlyRename);

    /// <summary>
    /// The target lies strictly inside the source's subtree (compared ignoring case, as the mesh
    /// keys paths): the copy would write into the subtree it is about to delete.
    /// </summary>
    public const string IntoOwnSubtree = nameof(IntoOwnSubtree);

    /// <summary>Source and target are different nodes and neither subtree contains the other's target.</summary>
    public const string Distinct = nameof(Distinct);
}

/// <summary>
/// Path arithmetic for <see cref="MoveNodeRequest"/> (#6951): classifying a target against its
/// source, and naming the staging sibling a case-only rename relocates through.
/// </summary>
internal static class MovePaths
{
    /// <summary>The marker a case-only rename's staging sibling carries after the target's leaf.</summary>
    public const string StagingMarker = "-recase-";

    /// <summary>
    /// Classifies <paramref name="target"/> against <paramref name="source"/> on whole path
    /// segments, ignoring case the way the mesh keys paths. Leading and trailing slashes are not
    /// part of a path. Returns one of the <see cref="MovePathRelation"/> constants.
    /// </summary>
    /// <param name="source">The node being moved.</param>
    /// <param name="target">Where it is being moved to.</param>
    public static string Classify(string source, string target)
    {
        var s = Normalize(source);
        var t = Normalize(target);
        if (string.Equals(s, t, StringComparison.Ordinal))
            return MovePathRelation.Same;
        if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase))
            return MovePathRelation.CaseOnlyRename;
        if (s.Length == 0
            || (t.Length > s.Length
                && t[s.Length] == '/'
                && t.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            return MovePathRelation.IntoOwnSubtree;
        return MovePathRelation.Distinct;
    }

    /// <summary>
    /// The staging sibling a case-only rename to <paramref name="target"/> passes through: under the TARGET's parent (so it needs the same
    /// Create grant the target does), named after the target's leaf plus <see cref="StagingMarker"/>
    /// and a fresh token, so it is <see cref="MovePathRelation.Distinct"/> from both ends in every
    /// store — case-sensitive or not.
    /// </summary>
    /// <param name="target">The new casing of the node being renamed.</param>
    /// <remarks>
    /// Distinct by construction: the staging leaf is the target's leaf EXTENDED (never followed by
    /// a <c>/</c>), so it neither equals nor lies under the source or the target, ignoring case or not.
    /// </remarks>
    public static string CaseRenameStaging(string target)
        => $"{Normalize(target)}{StagingMarker}{Guid.NewGuid().ToString("N")[..12]}";

    private static string Normalize(string? path) => path?.Trim('/') ?? string.Empty;
}
