using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// What became of ONE node of a subtree copy. The unit exists because a copy's only report used to
/// be a COUNT, and a count cannot say which member of a set is missing.
/// </summary>
public enum NodeCopyDisposition
{
    /// <summary>No node existed at the target path; this copy created it.</summary>
    Created,

    /// <summary>A node existed at the target path and this copy (<c>force</c>) overwrote it.</summary>
    Updated,

    /// <summary>
    /// A node already existed at the target path and <c>force</c> was not set, so the copy left it
    /// alone. 🚨 This COUNTS AS COVERED and deliberately so: the target path holds a node, which is
    /// the property a copy has to guarantee. It is not counted as COPIED — the count this operation
    /// returns has always meant "writes that happened", and a skip is not one.
    /// </summary>
    SkippedExisting,

    /// <summary>
    /// The write was attempted and did not come back acknowledged — refused, rejected, or faulted.
    ///
    /// <para>🚨 "Not acknowledged" is not "not written", and the wording is load-bearing in the
    /// same way <c>NodeReadOutcome</c>'s Unavailable is: a delivery that
    /// faults after the request left may well have landed. The report says what the copy was TOLD,
    /// which is the only thing it knows.</para>
    /// </summary>
    Failed,

    /// <summary>
    /// Never attempted, because a node at a SHALLOWER level of the same copy did not land. Writing
    /// it would produce a node under a parent that does not exist — the exact residue a failed copy
    /// used to leave.
    /// </summary>
    NotAttempted,
}

/// <summary>One node of a subtree copy, and what became of it.</summary>
/// <param name="SourcePath">Where it was read from.</param>
/// <param name="TargetPath">Where it was to land.</param>
/// <param name="Disposition">What happened.</param>
public sealed record NodeCopyEntry(
    string SourcePath, string TargetPath, NodeCopyDisposition Disposition)
{
    /// <summary>Why the write was not acknowledged; set only for
    /// <see cref="NodeCopyDisposition.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// True when the target path ends this copy holding a node — created, updated, or already
    /// there. This is the predicate the post-condition is expressed in.
    /// </summary>
    public bool Covered =>
        Disposition is NodeCopyDisposition.Created
            or NodeCopyDisposition.Updated
            or NodeCopyDisposition.SkippedExisting;

    /// <summary>True when this copy performed a write. The returned count sums these.</summary>
    public bool Written =>
        Disposition is NodeCopyDisposition.Created or NodeCopyDisposition.Updated;
}

/// <summary>
/// How a subtree copy ended — the distinction an <c>int</c> cannot carry, and the reason
/// <c>rbuergi/OperationRequest</c> reached a user partition without its <c>Source/</c> subtree
/// while the copy reported the count it managed. See
/// <c>Doc/Architecture/CopyCompleteness</c>.
/// </summary>
public enum NodeCopyStatus
{
    /// <summary>
    /// Every node the source subtree holds now has a node at its target path. The ONLY status a
    /// caller may read as success.
    /// </summary>
    Copied,

    /// <summary>Nothing resolved at the source path. Nothing was written.</summary>
    SourceNotFound,

    /// <summary>
    /// The copy's own enumeration of the source came back SHORT of what the source holds: nodes
    /// exist under it that this caller may not read, so a copy would silently leave them behind.
    /// Refused before anything was written.
    /// </summary>
    SourceNotFullyReadable,

    /// <summary>
    /// Whether the enumeration was complete could NOT be established. 🚨 Not a success and not a
    /// failure of the source — it is the "nothing was checked" answer, kept apart from
    /// <see cref="Copied"/> for the reason <c>Doc/Architecture/ControlsThatCannotFail</c> gives:
    /// a completeness check with nothing to check against would pass. Refused before anything was
    /// written.
    /// </summary>
    CompletenessNotDetermined,

    /// <summary>
    /// The writes ran and the target set is short of the enumerated set. <see cref="NodeCopyOutcome.Shortfall"/>
    /// names every path, whether it failed or was never attempted.
    /// </summary>
    Incomplete,
}

/// <summary>
/// The outcome of copying a subtree — <see cref="Status"/> plus the per-node ledger it was decided
/// from. Modelled on <c>NodeDiagnosticsOutcome</c>, and for the
/// same reason: the operation underneath already distinguishes "carried everything" from "carried
/// what it could see", and the count was discarding that distinction rather than lacking it.
/// </summary>
public sealed record NodeCopyOutcome
{
    /// <summary>What the copy established.</summary>
    public required NodeCopyStatus Status { get; init; }

    /// <summary>The subtree root that was copied.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The path the subtree root was copied to.</summary>
    public required string TargetPath { get; init; }

    /// <summary>
    /// One entry per node the copy enumerated. Empty for the statuses that refuse before writing —
    /// there is no ledger because nothing was attempted.
    /// </summary>
    public ImmutableList<NodeCopyEntry> Entries { get; init; } = [];

    /// <summary>How many nodes the caller's own enumeration of the source returned.</summary>
    public int EnumeratedCount { get; init; }

    /// <summary>
    /// How many nodes the source subtree HOLDS, established independently of what the caller may
    /// read. Equal to <see cref="EnumeratedCount"/> whenever the enumeration is complete; -1 when
    /// it could not be established (<see cref="NodeCopyStatus.CompletenessNotDetermined"/>).
    /// </summary>
    public int SourceNodeCount { get; init; } = -1;

    /// <summary>Why nothing could be established; set only for
    /// <see cref="NodeCopyStatus.CompletenessNotDetermined"/> and
    /// <see cref="NodeCopyStatus.SourceNotFound"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// True only for <see cref="NodeCopyStatus.Copied"/>, so a caller that reads nothing but this
    /// flag still cannot mistake a refusal — or a shortfall — for a finished copy.
    /// </summary>
    public bool IsComplete => Status == NodeCopyStatus.Copied;

    /// <summary>
    /// Writes this copy performed. 🚨 Deliberately NOT the size of the subtree: a node already
    /// present at its target under <c>force=false</c> is covered and not copied, which is what the
    /// count has always meant.
    /// </summary>
    public int CopiedCount => Entries.Count(e => e.Written);

    /// <summary>Every enumerated node whose target path does NOT hold a node — the post-condition's
    /// difference, failures and never-attempted alike.</summary>
    public ImmutableList<NodeCopyEntry> Shortfall =>
        Entries.Where(e => !e.Covered).ToImmutableList();

    /// <summary>The copy carried everything it enumerated, and the enumeration was complete.</summary>
    /// <param name="sourcePath">The subtree root.</param>
    /// <param name="targetPath">Where it landed.</param>
    /// <param name="entries">The per-node ledger.</param>
    /// <param name="sourceNodeCount">The independently established size of the source subtree.</param>
    public static NodeCopyOutcome Copied(
        string sourcePath, string targetPath,
        ImmutableList<NodeCopyEntry> entries, int sourceNodeCount) =>
        new()
        {
            Status = NodeCopyStatus.Copied,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Entries = entries,
            EnumeratedCount = entries.Count,
            SourceNodeCount = sourceNodeCount,
        };

    /// <summary>The writes ran and the target set is short of the enumerated set.</summary>
    /// <param name="sourcePath">The subtree root.</param>
    /// <param name="targetPath">Where it was landing.</param>
    /// <param name="entries">The per-node ledger.</param>
    /// <param name="sourceNodeCount">The independently established size of the source subtree.</param>
    public static NodeCopyOutcome Incomplete(
        string sourcePath, string targetPath,
        ImmutableList<NodeCopyEntry> entries, int sourceNodeCount) =>
        new()
        {
            Status = NodeCopyStatus.Incomplete,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Entries = entries,
            EnumeratedCount = entries.Count,
            SourceNodeCount = sourceNodeCount,
        };

    /// <summary>Nothing resolved at the source path.</summary>
    /// <param name="sourcePath">The path that resolved to nothing.</param>
    /// <param name="targetPath">Where the copy would have landed.</param>
    public static NodeCopyOutcome SourceNotFound(string sourcePath, string targetPath) =>
        new()
        {
            Status = NodeCopyStatus.SourceNotFound,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Reason = $"Source node not found: {sourcePath}",
        };

    /// <summary>The enumeration is short of what the source holds. Nothing was written.</summary>
    /// <param name="sourcePath">The subtree root.</param>
    /// <param name="targetPath">Where the copy would have landed.</param>
    /// <param name="enumeratedCount">What the caller could see.</param>
    /// <param name="sourceNodeCount">What the subtree holds.</param>
    public static NodeCopyOutcome SourceNotFullyReadable(
        string sourcePath, string targetPath, int enumeratedCount, int sourceNodeCount) =>
        new()
        {
            Status = NodeCopyStatus.SourceNotFullyReadable,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            EnumeratedCount = enumeratedCount,
            SourceNodeCount = sourceNodeCount,
        };

    /// <summary>Completeness could not be established. Nothing was written.</summary>
    /// <param name="sourcePath">The subtree root.</param>
    /// <param name="targetPath">Where the copy would have landed.</param>
    /// <param name="reason">What was missing, named so it can be provisioned.</param>
    public static NodeCopyOutcome CompletenessNotDetermined(
        string sourcePath, string targetPath, string reason) =>
        new()
        {
            Status = NodeCopyStatus.CompletenessNotDetermined,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Reason = reason,
        };

    /// <summary>
    /// A human- and agent-readable account of a non-<see cref="NodeCopyStatus.Copied"/> outcome.
    /// Null when the copy completed.
    ///
    /// <para>🚨 A <see cref="NodeCopyStatus.SourceNotFullyReadable"/> refusal states COUNTS and
    /// never the paths it could not read. The shortfall is a permission fact about the READER, so
    /// naming what it hides would turn a refusal into a disclosure surface — the same reason
    /// #3890 closed the autocomplete drill-down. A
    /// <see cref="NodeCopyStatus.Incomplete"/> report DOES name its paths: every one of them came
    /// out of the caller's own enumeration, so it discloses nothing the caller cannot already
    /// read.</para>
    /// </summary>
    public string? Describe() => Status switch
    {
        NodeCopyStatus.Copied => null,

        NodeCopyStatus.SourceNotFound => Reason,

        NodeCopyStatus.SourceNotFullyReadable =>
            $"{NodeCopyHelper.IncompleteSourceRefusal} at '{SourcePath}': it holds {SourceNodeCount} "
            + $"node(s) and this caller can read {EnumeratedCount}, so {SourceNodeCount - EnumeratedCount} "
            + "would have been left behind. Nothing was written. A subtree copied without part of "
            + "itself is not a smaller copy — it is a broken one, which is why this is a refusal "
            + "rather than a partial result.",

        NodeCopyStatus.CompletenessNotDetermined =>
            $"{NodeCopyHelper.UndeterminedCompletenessRefusal} at '{SourcePath}': {Reason}. Nothing "
            + "was written. This is NOT evidence that the subtree is short — it is the absence of "
            + "evidence either way, and a copy that cannot establish what it is copying must say so.",

        _ =>
            $"{NodeCopyHelper.IncompleteCopyReport} '{SourcePath}' -> '{TargetPath}': "
            + $"{EnumeratedCount} node(s) enumerated, {CopiedCount} written, "
            + $"{Entries.Count(e => e.Disposition == NodeCopyDisposition.SkippedExisting)} already present, "
            + $"{Shortfall.Count} did not land:"
            + string.Concat(Shortfall.Select(e => Environment.NewLine
                + (e.Disposition == NodeCopyDisposition.Failed
                    ? $"  not acknowledged  {e.TargetPath} — {e.Error}"
                    : $"  not attempted     {e.TargetPath} — a node above it did not land")))
            + Environment.NewLine
            + "What DID land is left in place: this copy may have overwritten nodes it holds no "
            + "before-image of, so deleting them back would be a second uncontrolled mutation "
            + "rather than a rollback.",
    };
}
