using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using AnchorMath = MeshWeaver.Markdown.Collaboration.AnchorMath;
using MarkdownAnnotationParser = MeshWeaver.Markdown.Collaboration.MarkdownAnnotationParser;

namespace MeshWeaver.Graph;

/// <summary>
/// Derives the tracked-change VIEW MODEL from a node's version history — nothing is stored.
/// <para>
/// The version history (<c>IVersionQuery</c>) is already the authoritative record of every change:
/// each version carries its author, its timestamp and its full content. So "what changed, by whom,
/// and when" is a projection over that record, not a second store: diff the chosen baseline version
/// against the current text, then attribute each resulting hunk to the version step that introduced
/// it. Nothing can go stale, nothing needs reconciling, and a *revert* is a normal versioned write
/// that itself lands in the history.
/// </para>
/// <para>
/// The produced <see cref="TrackedChange"/>s are pure view models: their ranges are already
/// resolved against the text they were projected from (<c>AnchorText</c> = that text), so a later
/// <see cref="ChangeRendering.ResolveEffective"/> against a newer text re-locates them through
/// <see cref="AnchorMath"/> exactly like it does for comments. They are never persisted — the
/// <c>_Tracking</c> satellites this replaces are legacy read-only rows.
/// </para>
/// </summary>
public static class ChangeProjection
{
    /// <summary>
    /// The most historical versions the projection loads for ONE comparison. Each costs a historical
    /// read, so a wide range is bounded rather than unbounded. The two endpoints are always loaded;
    /// when a range holds more intermediate versions than this, the OLDEST intermediates are dropped
    /// (see <see cref="Between"/>) — the redline is unaffected, only the per-hunk author attribution
    /// degrades to "unattributed" for hunks introduced in the dropped span. Stated, never silently
    /// exceeded.
    /// </summary>
    public const int MaxSteps = 10;

    /// <summary>
    /// Shortest trimmed text run that counts as attribution evidence. Shorter runs ("the ", "a ")
    /// match across unrelated edits and would attribute a hunk to the wrong author.
    /// </summary>
    public const int MinDistinctiveLength = 4;

    /// <summary>
    /// Characters of surrounding common text a hunk may grow by on each side to land on word
    /// boundaries. The engine diffs characters; expanding to whole words is what turns
    /// <c>ca[t→r]</c> into <c>[cat→car]</c> in the redline.
    /// </summary>
    private const int MaxWordExpansion = 40;

    /// <summary>
    /// One step of the version history: the version's metadata plus the document's clean text at
    /// that version. Ordered oldest → newest by the caller; the FIRST entry is the baseline and is
    /// never attributed (only entries after it are edits).
    /// </summary>
    public record VersionStep(long Version, string? Author, DateTimeOffset When, string CleanText);

    /// <summary>
    /// One contiguous difference between the baseline text and the current text. Offsets are
    /// character offsets into the respective full texts; <see cref="BaseText"/> is empty for a pure
    /// insertion, <see cref="CurrentText"/> for a pure deletion.
    /// </summary>
    public record Hunk(int BaseStart, string BaseText, int CurrentStart, string CurrentText);

    /// <summary>
    /// Computes the contiguous hunks turning <paramref name="baseText"/> into
    /// <paramref name="currentText"/>, reusing the character-level LCS engine
    /// (<see cref="AnchorMath.Diff"/>) that already anchors comments — one diff implementation, not
    /// two. Each hunk is then grown outwards to word boundaries through the surrounding COMMON text
    /// (identical in both texts by construction, so the growth is symmetric), and overlapping hunks
    /// are merged. Identical texts produce an empty list.
    /// </summary>
    public static IReadOnlyList<Hunk> Diff(string? baseText, string? currentText)
    {
        var a = baseText ?? "";
        var b = currentText ?? "";
        if (string.Equals(a, b, StringComparison.Ordinal))
            return [];

        // Raw ranges, plus the length of the common run on each side (the growth budget).
        var ranges = new List<(int BaseStart, int BaseEnd, int CurStart, int CurEnd, int Before, int After)>();
        var aPos = 0;
        var bPos = 0;
        var segments = AnchorMath.Diff(a, b);
        var equalBefore = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Op == AnchorMath.Op.Equal)
            {
                equalBefore = segment.Length;
                aPos += segment.Length;
                bPos += segment.Length;
                continue;
            }

            // A Delete immediately followed by an Insert (or vice versa) is ONE replacement hunk.
            var baseStart = aPos;
            var curStart = bPos;
            while (i < segments.Count && segments[i].Op != AnchorMath.Op.Equal)
            {
                if (segments[i].Op == AnchorMath.Op.Delete) aPos += segments[i].Length;
                else bPos += segments[i].Length;
                i++;
            }
            i--;
            var equalAfter = i + 1 < segments.Count ? segments[i + 1].Length : 0;
            ranges.Add((baseStart, aPos, curStart, bPos, equalBefore, equalAfter));
            equalBefore = 0;
        }

        // Grow to word boundaries through the common text — but ONLY on an edge that actually cuts a
        // word in half. A hunk that already starts/ends on whitespace is left alone; growing it
        // anyway would turn "insert 'brave '" into "replace 'world' with 'brave world'".
        var expanded = ranges
            .Select(r =>
            {
                var firstChanged = FirstChar(a, r.BaseStart, r.BaseEnd) ?? FirstChar(b, r.CurStart, r.CurEnd);
                var lastChanged = LastChar(a, r.BaseStart, r.BaseEnd) ?? LastChar(b, r.CurStart, r.CurEnd);
                var cutsLeft = r.CurStart > 0 && !char.IsWhiteSpace(b[r.CurStart - 1])
                               && firstChanged is { } first && !char.IsWhiteSpace(first);
                var cutsRight = r.CurEnd < b.Length && !char.IsWhiteSpace(b[r.CurEnd])
                                && lastChanged is { } last && !char.IsWhiteSpace(last);
                var before = cutsLeft ? GrowLeft(b, r.CurStart, Math.Min(r.Before, MaxWordExpansion)) : 0;
                var after = cutsRight ? GrowRight(b, r.CurEnd, Math.Min(r.After, MaxWordExpansion)) : 0;
                return (BaseStart: r.BaseStart - before, BaseEnd: r.BaseEnd + after,
                        CurStart: r.CurStart - before, CurEnd: r.CurEnd + after);
            })
            .ToList();

        var merged = new List<(int BaseStart, int BaseEnd, int CurStart, int CurEnd)>();
        foreach (var range in expanded)
        {
            if (merged.Count > 0 && range.CurStart <= merged[^1].CurEnd)
            {
                var last = merged[^1];
                merged[^1] = (last.BaseStart, Math.Max(last.BaseEnd, range.BaseEnd),
                              last.CurStart, Math.Max(last.CurEnd, range.CurEnd));
                continue;
            }
            merged.Add(range);
        }

        return merged
            .Select(r => new Hunk(
                r.BaseStart, a[r.BaseStart..r.BaseEnd],
                r.CurStart, b[r.CurStart..r.CurEnd]))
            .ToList();
    }

    private static char? FirstChar(string text, int start, int end) => end > start ? text[start] : null;

    private static char? LastChar(string text, int start, int end) => end > start ? text[end - 1] : null;

    // Characters to walk left from `at` (bounded by `budget`) to reach a whitespace boundary.
    private static int GrowLeft(string text, int at, int budget)
    {
        var grown = 0;
        while (grown < budget && at - grown - 1 >= 0 && !char.IsWhiteSpace(text[at - grown - 1]))
            grown++;
        return grown;
    }

    // Characters to walk right from `at` (bounded by `budget`) to reach a whitespace boundary.
    private static int GrowRight(string text, int at, int budget)
    {
        var grown = 0;
        while (grown < budget && at + grown < text.Length && !char.IsWhiteSpace(text[at + grown]))
            grown++;
        return grown;
    }

    /// <summary>
    /// Projects the tracked-change view models for <paramref name="currentClean"/> against
    /// <paramref name="steps"/>. <paramref name="steps"/> is ordered oldest → newest and its FIRST
    /// entry is the baseline the diff is taken against; the last entry is the current state.
    /// Every returned change is already resolved (<c>Effective*</c> set) against
    /// <paramref name="currentClean"/>.
    /// </summary>
    public static IReadOnlyList<TrackedChange> Project(
        string? primaryNodePath,
        IReadOnlyList<VersionStep> steps,
        string? currentClean,
        long currentVersion)
    {
        var current = currentClean ?? "";
        if (steps.Count < 2)
            return [];

        var hunks = Diff(steps[0].CleanText, current);
        if (hunks.Count == 0)
            return [];

        var attribution = Attribute(steps, hunks);
        return hunks
            .Select((hunk, index) =>
            {
                var step = attribution[index];
                var length = hunk.CurrentText.Length;
                return new TrackedChange
                {
                    Id = HunkId(hunk),
                    PrimaryNodePath = primaryNodePath,
                    MarkerId = HunkId(hunk),
                    ChangeType = ChangeRendering.Classify(hunk.BaseText, hunk.CurrentText),
                    Author = step?.Author ?? "",
                    CreatedAt = step?.When ?? steps[^1].When,
                    Status = TrackedChangeStatus.Pending,
                    Version = step?.Version ?? currentVersion,
                    Start = hunk.CurrentStart,
                    Length = length,
                    // The projection was taken against `current`, so that IS the anchor: a later
                    // ResolveEffective against a newer text re-locates the range through AnchorMath.
                    AnchorText = current,
                    OriginalText = hunk.BaseText.Length == 0 ? null : hunk.BaseText,
                    NewText = hunk.CurrentText.Length == 0 ? null : hunk.CurrentText,
                    EffectiveStart = hunk.CurrentStart,
                    EffectiveEnd = hunk.CurrentStart + length,
                    EffectiveVersion = currentVersion
                };
            })
            .ToList();
    }

    /// <summary>
    /// A stable, DOM-safe id for a hunk — derived from its position and both texts, so the same
    /// change keeps the same card / inline span across re-renders while two identical edits at
    /// different places stay distinct.
    /// </summary>
    private static string HunkId(Hunk hunk)
    {
        var payload = $"{hunk.CurrentStart}{hunk.BaseText}{hunk.CurrentText}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "h" + Convert.ToHexString(digest)[..11].ToLowerInvariant();
    }

    /// <summary>
    /// Attributes each of <paramref name="finalHunks"/> (the baseline → current diff) to the single
    /// step that introduced it, or <c>null</c> when no step — or several authors' steps — match.
    /// Each step is diffed against its predecessor; a final hunk is attributed to a step when the
    /// text it added (or removed) overlaps that step's insertions (or deletions).
    /// <para>
    /// This is a HEURISTIC and stays honest about it: matching is by text, not by position, so a
    /// hunk touched by several authors attributes to nobody rather than to the wrong one. Positional
    /// ancestry (mapping offsets through every step's diff) is the exact refinement if best-effort
    /// ever stops being enough.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VersionStep?> Attribute(
        IReadOnlyList<VersionStep> steps, IReadOnlyList<Hunk> finalHunks)
    {
        var edits = new List<(VersionStep Step, List<string> Inserted, List<string> Deleted)>();
        for (var i = 1; i < steps.Count; i++)
        {
            var stepHunks = Diff(steps[i - 1].CleanText, steps[i].CleanText);
            edits.Add((
                steps[i],
                stepHunks.Where(h => h.CurrentText.Length > 0).Select(h => h.CurrentText).ToList(),
                stepHunks.Where(h => h.BaseText.Length > 0).Select(h => h.BaseText).ToList()));
        }

        return finalHunks
            .Select(hunk =>
            {
                var candidates = edits
                    .Where(e =>
                        (hunk.CurrentText.Length > 0 && e.Inserted.Any(t => Overlaps(t, hunk.CurrentText)))
                        || (hunk.BaseText.Length > 0 && e.Deleted.Any(t => Overlaps(t, hunk.BaseText))))
                    .Select(e => e.Step)
                    .ToList();
                if (candidates.Count == 0)
                    return null;
                var authors = candidates.Select(c => c.Author ?? "").Distinct().ToList();
                // One author (possibly across several of their steps) → the latest of their steps;
                // several authors → ambiguous, attribute to nobody rather than to the wrong one.
                return authors.Count == 1 ? candidates.OrderBy(c => c.Version).Last() : null;
            })
            .ToList();
    }

    private static bool Overlaps(string stepText, string hunkText)
    {
        if (stepText.Trim().Length < MinDistinctiveLength || hunkText.Trim().Length < MinDistinctiveLength)
            return false;
        return stepText.Contains(hunkText, StringComparison.Ordinal)
            || hunkText.Contains(stepText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Projects the tracked changes that turned version <paramref name="fromVersion"/> of the node
    /// into <paramref name="toNode"/> — the node as the caller wants it compared, i.e. either its
    /// LIVE state or a historical snapshot already loaded from <see cref="IVersionQuery"/>.
    /// <para>
    /// The reader states the range; nothing is guessed. The baseline snapshot plus every version
    /// strictly between it and <paramref name="toNode"/> are loaded (capped at
    /// <paramref name="maxSteps"/>, oldest intermediates dropped first) purely so each hunk can be
    /// attributed to the step that introduced it — the redline itself is the endpoint-to-endpoint
    /// diff and is never affected by the cap.
    /// </para>
    /// Cold and bounded: <c>n</c> historical reads, run sequentially so a partition's single pooled
    /// connection is never fanned out. Emits once, then completes — a caller comparing against the
    /// live document re-subscribes (e.g. with <c>Switch</c>) when the node's version moves.
    /// </summary>
    /// <param name="versionQuery">The version store; <c>null</c> yields no changes.</param>
    /// <param name="toNode">The node state the baseline is compared TO.</param>
    /// <param name="fromVersion">The baseline version to compare FROM.</param>
    /// <param name="options">Serializer options used to materialise historical snapshots.</param>
    /// <param name="maxSteps">Upper bound on historical reads. See <see cref="MaxSteps"/>.</param>
    public static IObservable<IReadOnlyList<TrackedChange>> Between(
        IVersionQuery? versionQuery,
        MeshNode toNode,
        long fromVersion,
        JsonSerializerOptions options,
        int maxSteps = MaxSteps)
    {
        if (versionQuery is null || fromVersion >= toNode.Version)
            return Observable.Return<IReadOnlyList<TrackedChange>>([]);

        var path = toNode.Path;
        var toClean = CleanTextOf(toNode);
        var toStep = new VersionStep(
            toNode.Version, toNode.LastModifiedBy, toNode.LastModified, toClean);

        return versionQuery.GetVersions(path)
            .ToList()
            .SelectMany(all =>
            {
                // The baseline is the FIRST step and is what the diff is taken against, so it is
                // never one of the entries the cap may drop — hence Take on the newest and a
                // separate guarantee that fromVersion leads the list.
                var inRange = all
                    .Where(v => v.Version >= fromVersion && v.Version < toNode.Version)
                    .OrderByDescending(v => v.Version)
                    .Take(maxSteps - 1)
                    .Concat(all.Where(v => v.Version == fromVersion))
                    .DistinctBy(v => v.Version)
                    .OrderBy(v => v.Version)
                    .ToList();
                if (inRange.Count == 0 || inRange[0].Version != fromVersion)
                    return Observable.Return<IReadOnlyList<TrackedChange>>([]);

                return inRange
                    .Select(summary => versionQuery
                        .GetVersion(path, summary.Version, options)
                        .Take(1)
                        .Select(snapshot => snapshot is null
                            ? null
                            : new VersionStep(
                                summary.Version,
                                // The snapshot's own audit fields are the authority; the summary's
                                // ChangedBy fills in for stores that only carry it there.
                                string.IsNullOrEmpty(snapshot.LastModifiedBy) ? summary.ChangedBy : snapshot.LastModifiedBy,
                                snapshot.LastModified == default ? summary.LastModified : snapshot.LastModified,
                                CleanTextOf(snapshot))))
                    .Concat()
                    .ToList()
                    .Select(loaded =>
                    {
                        var steps = loaded.Where(s => s is not null).Select(s => s!).ToList();
                        // The baseline snapshot itself must have loaded — without it there is
                        // nothing to diff against, and diffing from a LATER step would silently
                        // report a narrower change set than the reader asked for.
                        if (steps.Count == 0 || steps[0].Version != fromVersion)
                            return (IReadOnlyList<TrackedChange>)[];
                        steps.Add(toStep);
                        return Project(path, steps, toClean, toNode.Version);
                    });
            });
    }

    /// <summary>
    /// The document's CLEAN markdown text — shape-tolerant (typed / string / <c>JsonElement</c>
    /// content) and with any legacy inline annotation markers stripped, so a historical snapshot
    /// written before the markers were retired still diffs against today's text.
    /// </summary>
    public static string CleanTextOf(MeshNode? node)
    {
        var markdown = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
        return string.IsNullOrEmpty(markdown) ? "" : MarkdownAnnotationParser.StripAllMarkers(markdown);
    }
}
