namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Bounds how many concurrent READ operations a storage adapter may run against
/// its backing store. Sized <b>per storage adapter</b> (see
/// <see cref="PostgreSqlStorageOptions.MaxReadConcurrency"/>): the Postgres adapter
/// caps reads <i>below</i> its connection-pool size so a synced-query fan-out storm
/// (e.g. a flood of <c>ListChildPaths</c>/descendants walks across many sessions)
/// cannot drain the pool and starve <i>writes</i> — onboarding, chat, etc. stay
/// ungated and always keep pool headroom. An in-memory adapter has no connection
/// scarcity and simply doesn't create a gate (reads run unbounded).
///
/// <para>🚨 Instance-owned by the mesh-scoped partition provider — never static —
/// so it dies with the mesh. All adapters the provider creates share ONE gate
/// because they share ONE connection pool.</para>
///
/// <para>Prod 2026-06-04: an un-gated read fan-out pinned the 50-connection pool
/// ("connection pool has been exhausted, currently 50") and blocked onboarding.</para>
/// </summary>
public sealed class ReadConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _slots;

    /// <summary>Maximum concurrent read operations permitted.</summary>
    public int MaxConcurrency { get; }

    public ReadConcurrencyGate(int maxConcurrency)
    {
        MaxConcurrency = maxConcurrency < 1 ? 1 : maxConcurrency;
        _slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    /// <summary>Acquire a slot. Pair with exactly one <see cref="Release"/> in a finally.</summary>
    public Task WaitAsync(CancellationToken ct) => _slots.WaitAsync(ct);

    /// <summary>Release a previously-acquired slot.</summary>
    public void Release() => _slots.Release();

    /// <summary>
    /// Acquire a slot and return a releaser. <c>using var _ = await gate.AcquireAsync(ct);</c>
    /// holds the slot for the scope — safe inside an <c>async IAsyncEnumerable</c> iterator
    /// (the slot is released when the enumerator is disposed). The releaser is idempotent.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_slots);
    }

    /// <summary>Slots currently free (diagnostics/tests).</summary>
    public int CurrentCount => _slots.CurrentCount;

    public void Dispose() => _slots.Dispose();

    private sealed class Releaser(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
