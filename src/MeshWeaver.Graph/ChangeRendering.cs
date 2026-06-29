using MeshWeaver.Mesh;
using AnchorMath = MeshWeaver.Markdown.Collaboration.AnchorMath;

namespace MeshWeaver.Graph;

/// <summary>
/// Capture, version-delta resolution, and accept/reject application for tracked changes (suggested
/// edits). Mirrors <see cref="CommentRendering"/> for comments: the document text is kept CLEAN, the
/// change records its range/anchor, and the diff view is re-derived against the current text.
/// Accepting a change applies <see cref="TrackedChange.NewText"/> to the document; rejecting drops
/// the satellite.
/// </summary>
public static class ChangeRendering
{
    /// <summary>
    /// Classifies an edit from the deleted/inserted text pair.
    /// </summary>
    public static TrackedChangeType Classify(string? deletedText, string? insertedText)
    {
        var hasDelete = !string.IsNullOrEmpty(deletedText);
        var hasInsert = !string.IsNullOrEmpty(insertedText);
        return hasDelete && hasInsert ? TrackedChangeType.Replacement
            : hasDelete ? TrackedChangeType.Deletion
            : TrackedChangeType.Insertion;
    }

    /// <summary>
    /// Recomputes the effective range for <paramref name="change"/> against the current clean text and
    /// version, returning a copy with the <c>Effective*</c> fields set.
    /// </summary>
    public static TrackedChange ResolveEffective(TrackedChange change, string currentClean, long currentVersion)
    {
        currentClean ??= "";

        if (change.Start < 0)
            return change with { EffectiveStart = -1, EffectiveEnd = -1, EffectiveVersion = currentVersion };

        int start, end;
        if (change.AnchorText is not null)
        {
            (start, end) = AnchorMath.Resolve(change.AnchorText, change.Start, change.Length, currentClean);
        }
        else if (!string.IsNullOrEmpty(change.OriginalText))
        {
            // Legacy / no captured anchor — relocate by the original text.
            var idx = currentClean.IndexOf(change.OriginalText, StringComparison.Ordinal);
            if (idx < 0)
                return change with { EffectiveStart = -1, EffectiveEnd = -1, EffectiveVersion = currentVersion };
            (start, end) = (idx, idx + change.OriginalText.Length);
        }
        else
        {
            // Pure insertion with no anchor text — clamp the captured point into range.
            start = Math.Clamp(change.Start, 0, currentClean.Length);
            end = start;
        }

        return change with { EffectiveStart = start, EffectiveEnd = end, EffectiveVersion = currentVersion };
    }

    /// <summary>Resolves a whole set of changes against the current text.</summary>
    public static IReadOnlyList<TrackedChange> ResolveAll(
        IEnumerable<TrackedChange> changes, string currentClean, long currentVersion)
        => changes.Select(c => ResolveEffective(c, currentClean, currentVersion)).ToList();

    /// <summary>
    /// Applies an ACCEPTED change to <paramref name="clean"/> at its effective range, returning the new
    /// clean content. (Reject is a no-op on the text — just drop the satellite.)
    /// </summary>
    public static string Apply(string clean, TrackedChange resolvedChange)
    {
        clean ??= "";
        var start = Math.Clamp(resolvedChange.EffectiveStart, 0, clean.Length);
        var end = Math.Clamp(resolvedChange.EffectiveEnd, start, clean.Length);
        var newText = resolvedChange.NewText ?? "";

        return resolvedChange.ChangeType switch
        {
            TrackedChangeType.Insertion => clean.Insert(start, newText),
            TrackedChangeType.Deletion => clean.Remove(start, end - start),
            TrackedChangeType.Replacement => clean.Remove(start, end - start).Insert(start, newText),
            _ => clean
        };
    }

    /// <summary>
    /// Applies a batch of changes (Accept All). Resolves each against the current text, then applies
    /// them from the end backwards so earlier offsets stay valid.
    /// </summary>
    public static string ApplyAll(string clean, IEnumerable<TrackedChange> changes, long currentVersion)
    {
        clean ??= "";
        var resolved = ResolveAll(changes, clean, currentVersion)
            .Where(c => c.EffectiveStart >= 0)
            .OrderByDescending(c => c.EffectiveStart)
            .ToList();

        var result = clean;
        foreach (var change in resolved)
            result = Apply(result, change);
        return result;
    }
}
