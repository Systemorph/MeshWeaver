using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The ONE view of "does this node's NodeType own its partition?" that the checks of a SINGLE node
/// operation share — carried on <see cref="NodeValidationContext"/>, which is built afresh per
/// operation, so the memo's lifetime IS that operation's and it can leak across neither operations
/// nor users. Never a cache: nothing here is keyed by anything an earlier operation wrote, and there
/// is no eviction to get wrong.
///
/// <para><b>Why it exists.</b> For a type declared in mesh content one resolution is TWO round trips
/// (an anchored listing that establishes the definition exists, then an authoritative stream read of
/// it as System). A top-level create of such a type is judged four times — RLS, the write guard, the
/// provisioning validator and the post-creation handler — and the first three run back to back
/// against the same context, so without this they resolved the same fact three times over: six reads
/// where two will do.</para>
///
/// <para>🚨 <b>This is a semantic change, not only a saving, and it is the intended one.</b> Three
/// independent resolutions could DISAGREE if the declaration were edited between them, and the
/// disagreement failed the create closed (MeshWeaver#4447). Sharing one resolution removes that
/// detection for those three — deliberately: a create should be judged against ONE view of its type,
/// and the window it covered is the microseconds between two validators of the same
/// <c>Concat</c>. The window that is actually wide — validation, then the storage write, then the
/// post-creation handler — is NOT collapsed: that handler resolves independently, on purpose, and
/// its disagreement still fails the create. See <c>Doc/Architecture/PartitionOwnershipResolution</c>.</para>
///
/// <para><b>Fail-closed is preserved by construction.</b> The memoized value is the resolver's
/// tri-state, <c>null</c> ("could not be established") included — so a resolution that starved is
/// remembered AS starved and every later check answers <c>Undetermined</c>, exactly as it would have
/// on its own. A stored <c>null</c> is a recorded decision, never an absent entry.</para>
/// </summary>
public sealed class PartitionOwnershipMemo
{
    // Keyed by node type, so a context COPIED for a different node (`context with { Node = … }`)
    // can never read an answer that belongs to another type. Immutable + ImmutableInterlocked: no
    // lock, no gate, and the first answer recorded is the one everybody sees.
    //
    // Ordinal (the shared Empty singleton, so an unused memo allocates NOTHING beyond itself — a
    // NodeValidationContext is built per node on a filtered read, not only per write). Exact
    // matching is also the safer rule: the four asks of one operation carry the very same string,
    // and a differently-cased type is resolved again rather than answered from another key.
    private ImmutableDictionary<string, bool?> answers = ImmutableDictionary<string, bool?>.Empty;

    private int resolutions;

    /// <summary>
    /// How many times this operation actually ran the resolver — <c>1</c> for an operation whose
    /// checks share the answer, and what a regression to per-check resolution shows up as.
    /// Diagnostic: nothing branches on it.
    /// </summary>
    public int Resolutions => Volatile.Read(ref resolutions);

    /// <summary>
    /// The answer for <paramref name="nodeType"/> in THIS operation: the one already recorded, or
    /// <paramref name="resolve"/>'s, recorded as it passes.
    /// </summary>
    /// <param name="nodeType">The NodeType being judged. Must not be empty.</param>
    /// <param name="resolve">
    /// The resolver, run at most once per operation and per type. Cold — deferred to subscribe, so a
    /// check that composes this observable without subscribing costs nothing and counts as nothing.
    /// </param>
    /// <returns>The tri-state: owns / does not own / could not be established.</returns>
    public IObservable<bool?> Once(string nodeType, Func<IObservable<bool?>> resolve)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeType);
        ArgumentNullException.ThrowIfNull(resolve);

        // Defer, so the lookup happens at SUBSCRIBE: the checks of one operation run sequentially
        // (the validator chain is a Concat), so by the time the second subscribes the first has
        // already settled and there is nothing left to share but the value.
        return Observable.Defer(() =>
        {
            if (Volatile.Read(ref answers).TryGetValue(nodeType, out var settled))
                return Observable.Return(settled);

            // A FAULT is not an answer: nothing is recorded, so the next check resolves afresh
            // rather than replaying a terminal — the same rule PromiseCache states as
            // "success is cached, failure is evicted".
            Interlocked.Increment(ref resolutions);
            // GetOrAdd RETURNS what ended up stored, and that is what is emitted — so even if two
            // resolutions somehow overlapped, both callers would still see one answer.
            return resolve().Select(answer => ImmutableInterlocked.GetOrAdd(ref answers, nodeType, answer));
        });
    }
}
