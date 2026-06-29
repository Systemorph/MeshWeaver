using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Injects the transient inline decorations for a collaborative markdown render: comment highlights
/// and the tracked-change diff view (insertions / deletions / replacements). All decorations are
/// derived from the satellite nodes at their resolved effective ranges and exist only for this one
/// render — the stored document text is never modified.
/// </summary>
public static class CollaborativeRenderer
{
    private readonly record struct Insertion(int Pos, int Order, string Text);

    /// <summary>
    /// Decorates <paramref name="clean"/> with comment-highlight spans and tracked-change diff spans.
    /// Comments and changes must already be resolved (effective ranges set). Only PENDING changes are
    /// shown.
    /// </summary>
    public static string Decorate(
        string clean,
        IReadOnlyCollection<Comment> resolvedComments,
        IReadOnlyCollection<TrackedChange> resolvedChanges)
    {
        if (string.IsNullOrEmpty(clean))
            return clean ?? "";

        var hasComments = resolvedComments is { Count: > 0 };
        var hasChanges = resolvedChanges is { Count: > 0 };
        if (!hasComments && !hasChanges)
            return clean;

        // Build a list of point insertions, then apply them from the end backwards so the positions
        // (in original clean coordinates) stay valid. Order disambiguates same-position insertions:
        // 0 = closing tags (leftmost), 1 = opening tags, 2 = inserted content.
        var inserts = new List<Insertion>();

        if (hasComments)
        {
            foreach (var c in resolvedComments!)
            {
                if (string.IsNullOrEmpty(c.MarkerId) || c.EffectiveStart < 0 || c.EffectiveEnd <= c.EffectiveStart)
                    continue;
                inserts.Add(new Insertion(c.EffectiveStart, 1,
                    $"<span class=\"comment-highlight\" data-comment-id=\"{c.MarkerId}\">"));
                inserts.Add(new Insertion(c.EffectiveEnd, 0, "</span>"));
            }
        }

        if (hasChanges)
        {
            foreach (var ch in resolvedChanges!)
            {
                if (ch.Status != TrackedChangeStatus.Pending || string.IsNullOrEmpty(ch.MarkerId) || ch.EffectiveStart < 0)
                    continue;
                var id = ch.MarkerId;
                var newText = ch.NewText ?? "";
                var hasRange = ch.EffectiveEnd > ch.EffectiveStart;

                switch (ch.ChangeType)
                {
                    case TrackedChangeType.Deletion when hasRange:
                        inserts.Add(new Insertion(ch.EffectiveStart, 1,
                            $"<span class=\"track-delete\" data-change-id=\"{id}\">"));
                        inserts.Add(new Insertion(ch.EffectiveEnd, 0, "</span>"));
                        break;

                    case TrackedChangeType.Replacement when hasRange:
                        inserts.Add(new Insertion(ch.EffectiveStart, 1,
                            $"<span class=\"track-delete\" data-change-id=\"{id}\">"));
                        inserts.Add(new Insertion(ch.EffectiveEnd, 0,
                            $"</span><span class=\"track-insert\" data-change-id=\"{id}\">{newText}</span>"));
                        break;

                    // Insertion, or a degenerate delete/replace with no range → show the added text.
                    default:
                        inserts.Add(new Insertion(ch.EffectiveStart, 2,
                            $"<span class=\"track-insert\" data-change-id=\"{id}\">{newText}</span>"));
                        break;
                }
            }
        }

        var result = clean;
        foreach (var ins in inserts.OrderByDescending(i => i.Pos).ThenByDescending(i => i.Order))
        {
            var pos = Math.Clamp(ins.Pos, 0, result.Length);
            result = result.Insert(pos, ins.Text);
        }
        return result;
    }
}
