using System.Collections.Concurrent;
using System.Reactive.Disposables;

namespace MeshWeaver.Data;

/// <summary>
/// Records which type sources of a data source have NOT yet settled their initial load, so the
/// <see cref="DataContext"/> init time-box can name what it was waiting on.
///
/// <para>🚨 <b>Why this has to exist (Systemorph/MeshWeaver#1122).</b> A data source builds its
/// initial <c>EntityStore</c> by fanning out over EVERY registered type source and aggregating
/// (<c>SelectMany</c> + <c>Aggregate</c> + <c>FirstAsync</c>). <c>Aggregate</c> emits only when the
/// whole fan-out COMPLETES, so a single type source that neither emits nor completes hangs the data
/// source, which hangs the <c>sync/</c> sub-hub's <c>BuildupAction</c>, which is what
/// <c>IDataSource.Initialized</c> waits on, which is what the 120 s time-box expires against. Every
/// one of those layers knew only "something did not finish"; the resulting <c>TimeoutException</c>
/// therefore GUESSED — <i>"likely a stuck NodeType compile, or a data source that never
/// initialised"</i> — and 217 occurrences across five weeks folded into one issue under one
/// fingerprint, mixing at least three unrelated populations because the message could not tell them
/// apart. This ledger is the missing measurement.</para>
///
/// <para><b>Diagnostic only.</b> Nothing waits on it, nothing branches on it, and an entry that
/// lingers costs one short string. It must never become a control signal — the time-box stays the
/// only thing that decides the outcome.</para>
///
/// <para><b>Instance, never static</b> (<c>Doc/Architecture/NoStaticState.md</c>): held as a
/// readonly field on the data source, so its lifetime is that data source's. Data sources are
/// records, so <c>with</c>-copies made during configuration share this reference — harmless and
/// deliberate, the same reasoning <c>DataContext</c>'s watchdog-disarm subject documents: only the
/// final instance is ever initialized.</para>
/// </summary>
internal sealed class TypeSourceInitializationLedger
{
    private readonly ConcurrentDictionary<string, ITypeSource> pending = new(StringComparer.Ordinal);

    /// <summary>
    /// The unsettled legs, as <c>{streamId}/{collectionName}</c> — followed by
    /// <c> [what inside the leg is outstanding]</c> when the type source implements
    /// <see cref="IReportsInitialLoadProgress"/>. A snapshot — the fan-out keeps running while a
    /// diagnostic reads this.
    /// </summary>
    internal IReadOnlyCollection<string> Pending => pending
        .Select(kv => kv.Value is IReportsInitialLoadProgress progress
                      && progress.DescribeInitialLoadProgress() is { Length: > 0 } detail
            ? $"{kv.Key} [{detail}]"
            : kv.Key)
        .ToArray();

    /// <summary>
    /// Marks every type source of <paramref name="stream"/> unsettled and hands back the claim the
    /// fan-out settles legs through. The claim is released when the STREAM is disposed — never when
    /// the initialization chain is, because the time-box expiring is exactly what disposes that
    /// chain, and releasing there would empty the ledger in the instant the diagnostic reads it.
    /// </summary>
    /// <param name="stream">The stream whose initial load is starting.</param>
    /// <param name="typeSources">The type sources that fan-out will wait on.</param>
    /// <returns>The claim used to settle individual legs.</returns>
    internal StreamClaim Claim(
        ISynchronizationStream<EntityStore> stream,
        IEnumerable<ITypeSource> typeSources)
    {
        var streamId = stream.StreamId;
        var legs = typeSources.Select(ts => (Key: Key(streamId, ts), Source: ts)).ToArray();
        foreach (var leg in legs)
            pending[leg.Key] = leg.Source;
        var keys = legs.Select(l => l.Key).ToArray();

        // Bounds growth on a data source whose partition streams churn: without this, every
        // stream that is torn down before settling leaves its legs behind forever.
        stream.RegisterForDisposal(Disposable.Create(() =>
        {
            foreach (var key in keys)
                pending.TryRemove(key, out _);
        }));

        return new StreamClaim(this, streamId);
    }

    private static string Key(string streamId, ITypeSource typeSource)
        => $"{streamId}/{typeSource.CollectionName}";

    /// <summary>
    /// One stream's claim on the ledger. <see cref="Settle"/> is called for a leg that emitted,
    /// completed empty, or faulted — all three are "this leg is no longer what we are waiting for".
    /// </summary>
    /// <param name="ledger">The owning ledger.</param>
    /// <param name="streamId">The stream this claim belongs to.</param>
    internal readonly struct StreamClaim(TypeSourceInitializationLedger ledger, string streamId)
    {
        /// <summary>Records that <paramref name="typeSource"/>'s leg has settled.</summary>
        /// <param name="typeSource">The type source whose initial load has settled.</param>
        internal void Settle(ITypeSource typeSource)
            => ledger.pending.TryRemove(Key(streamId, typeSource), out _);
    }
}
