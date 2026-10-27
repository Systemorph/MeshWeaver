using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// The sets of preference keys one inbox page has seeded into the person's <see cref="Mesh.InboxPreferences"/> node,
/// each IN FLIGHT or CONFIRMED. A set is done only once the owner confirmed its write: a pass that runs while the write
/// is in flight sees the node as it was before the write, and that is a stale read, not a loop (#7260). A failed
/// write is released, so a later pass may write it again. One instance per page; nothing here is static.
/// </summary>
internal sealed class InboxSeedGuard
{
    private ImmutableDictionary<string, bool> seeds = ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>Starts the write of <paramref name="fingerprint"/>; false when it is already in flight or confirmed.</summary>
    /// <param name="fingerprint">The missing keys, joined.</param>
    public bool TryBegin(string fingerprint)
    {
        var begun = false;
        ImmutableInterlocked.Update(ref seeds, s => (begun = !s.ContainsKey(fingerprint)) ? s.Add(fingerprint, false) : s);
        return begun;
    }

    /// <summary>The owner committed the write of <paramref name="fingerprint"/>: it is never written again by this page.</summary>
    /// <param name="fingerprint">The missing keys, joined.</param>
    public void Confirm(string fingerprint) => ImmutableInterlocked.Update(ref seeds, s => s.SetItem(fingerprint, true));

    /// <summary>The write of <paramref name="fingerprint"/> failed: a later pass may begin it again. A confirmed one stays confirmed.</summary>
    /// <param name="fingerprint">The missing keys, joined.</param>
    public void Release(string fingerprint)
        => ImmutableInterlocked.Update(ref seeds, s => s.TryGetValue(fingerprint, out var confirmed) && !confirmed ? s.Remove(fingerprint) : s);

    /// <summary>Whether the owner confirmed the write of <paramref name="fingerprint"/>.</summary>
    /// <param name="fingerprint">The missing keys, joined.</param>
    public bool IsConfirmed(string fingerprint) => seeds.TryGetValue(fingerprint, out var confirmed) && confirmed;
}
