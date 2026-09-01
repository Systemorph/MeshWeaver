namespace MeshWeaver.Testing.Xunit.Test;

/// <summary>
/// The end-to-end proof that a collection-scoped resource is actually TORN DOWN — the half a
/// static cache never had, and the reason the estate's <c>ShareMeshAcrossTests</c> sharing was
/// switched off (a pinned mesh outlived its tests and interfered with the next class's).
///
/// <para>An xunit v3 assembly fixture is disposed after every collection of the assembly has
/// finished, so this is the only place from which "every collection that booted a resource also
/// disposed it, exactly once" can be asserted. A failure here surfaces as an assembly cleanup
/// failure and reds the run — it cannot pass silently.</para>
/// </summary>
public sealed class DisposalAudit : IAsyncDisposable
{
    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        var booted = CountedResource.Boots.Keys.Order(StringComparer.Ordinal).ToList();
        if (booted.Count == 0)
            throw new InvalidOperationException(
                "no collection ever booted a resource — the execution host did not run the suite");

        var wrong = booted
            .Where(c => CountedResource.Disposals.GetValueOrDefault(c) != 1)
            .Select(c => $"{c}: booted {CountedResource.Boots[c]}x, "
                         + $"disposed {CountedResource.Disposals.GetValueOrDefault(c)}x")
            .ToList();

        if (wrong.Count > 0)
            throw new InvalidOperationException(
                "collection-scoped resources were not disposed exactly once — "
                + string.Join(" | ", wrong));

        return ValueTask.CompletedTask;
    }
}
