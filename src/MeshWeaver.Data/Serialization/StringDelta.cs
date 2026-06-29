namespace MeshWeaver.Data.Serialization;

/// <summary>
/// A minimal single-splice text delta: replace the range
/// <c>[<see cref="Start"/>, <see cref="Start"/> + <see cref="RemovedLength"/>)</c>
/// of a base string with <see cref="Inserted"/>.
///
/// <para>Computed via common-prefix + common-suffix, so a one-spot edit to a
/// large string produces a delta whose size is proportional to the edit, not to
/// the whole string. This is what lets a cross-hub MeshNode write carry only the
/// changed span of a big text field in its patch instead of the entire value.</para>
///
/// <para>Two deltas computed from the SAME base that touch disjoint ranges merge
/// cleanly (<see cref="ApplyAll"/>) — concurrent edits to different parts of a
/// document land together. Overlapping ranges are a genuine conflict
/// (<see cref="Overlaps"/>) the caller must resolve by version.</para>
/// </summary>
public readonly record struct StringDelta(int Start, int RemovedLength, string Inserted)
{
    /// <summary>True when the delta changes nothing (base equals target).</summary>
    public bool IsEmpty => RemovedLength == 0 && Inserted.Length == 0;

    /// <summary>
    /// Computes the minimal splice that turns <paramref name="oldValue"/> into
    /// <paramref name="newValue"/> by stripping the common prefix and the common
    /// suffix. Null is treated as empty.
    /// </summary>
    public static StringDelta Compute(string? oldValue, string? newValue)
    {
        oldValue ??= string.Empty;
        newValue ??= string.Empty;
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            return new StringDelta(0, 0, string.Empty);

        var maxPrefix = Math.Min(oldValue.Length, newValue.Length);
        var prefix = 0;
        while (prefix < maxPrefix && oldValue[prefix] == newValue[prefix])
            prefix++;

        // Common suffix, not eating into the already-matched prefix on either side.
        var maxSuffix = Math.Min(oldValue.Length, newValue.Length) - prefix;
        var suffix = 0;
        while (suffix < maxSuffix
               && oldValue[oldValue.Length - 1 - suffix] == newValue[newValue.Length - 1 - suffix])
            suffix++;

        var removed = oldValue.Length - prefix - suffix;
        var inserted = newValue.Substring(prefix, newValue.Length - prefix - suffix);
        return new StringDelta(prefix, removed, inserted);
    }

    /// <summary>Applies this delta to <paramref name="current"/> (null = empty).</summary>
    public string Apply(string? current)
    {
        current ??= string.Empty;
        // Clamp defensively so a delta computed against a slightly different base
        // (e.g. a stale mirror) splices in-bounds instead of throwing.
        var start = Math.Clamp(Start, 0, current.Length);
        var end = Math.Clamp(start + RemovedLength, start, current.Length);
        return string.Concat(current.AsSpan(0, start), Inserted, current.AsSpan(end));
    }

    /// <summary>
    /// True when two deltas computed from the same base touch overlapping (or
    /// abutting on a removal) ranges and therefore cannot be merged blindly.
    /// Pure inserts at the SAME position are treated as overlapping (ambiguous
    /// ordering) and left to version-based conflict resolution.
    /// </summary>
    public static bool Overlaps(StringDelta a, StringDelta b)
    {
        var aEnd = a.Start + a.RemovedLength;
        var bEnd = b.Start + b.RemovedLength;
        // Disjoint if one ends strictly before the other starts.
        if (aEnd < b.Start || bEnd < a.Start)
            return false;
        // Touching only at a single boundary point with NO removal on the touching
        // side is a clean adjacency (two inserts at distinct points). Same exact
        // insertion point with both pure inserts is ambiguous → overlap.
        if (aEnd == b.Start || bEnd == a.Start)
            return a.Start == b.Start; // only the identical-point insert case overlaps
        return true;
    }

    /// <summary>
    /// Applies several deltas that were each computed against the SAME
    /// <paramref name="baseValue"/>. Disjoint deltas are applied right-to-left
    /// (highest <see cref="Start"/> first) so earlier offsets stay valid.
    /// Throws <see cref="InvalidOperationException"/> if any two overlap — the
    /// caller resolves those by version.
    /// </summary>
    public static string ApplyAll(string? baseValue, IReadOnlyList<StringDelta> deltas)
    {
        var effective = deltas.Where(d => !d.IsEmpty).ToList();
        for (var i = 0; i < effective.Count; i++)
            for (var j = i + 1; j < effective.Count; j++)
                if (Overlaps(effective[i], effective[j]))
                    throw new InvalidOperationException(
                        $"Overlapping string deltas at [{effective[i].Start}..] and [{effective[j].Start}..] — resolve by version.");

        var result = baseValue ?? string.Empty;
        foreach (var d in effective.OrderByDescending(d => d.Start))
            result = d.Apply(result);
        return result;
    }
}
