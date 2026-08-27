using System;
using MeshWeaver.Mesh;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the update queue's conflict-retry POLICY (<see cref="MeshNodeStreamCache.ShouldRetryConflict"/>).
///
/// <para>The owner's Conflict NACK carries its own prescription — "re-read and re-apply" — and the
/// per-path update queue re-invokes the caller's mutation on the CURRENT mirror on every attempt,
/// so a bounded re-enqueue IS that prescription. Before this policy existed the NACK surfaced to
/// the caller: measured 2026-08-27 on memex, two pods stamping compile status on the SAME NodeType
/// node ('Store/Plugin') conflicted, type preparation died on the surfaced exception, and every
/// course cover in the store rendered the "build did not settle" fallback for hours (#2463's class).</para>
/// </summary>
public class ConflictRetryPolicyTest
{
    private static Exception Conflict() =>
        new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.Conflict, "Store/Plugin",
            "cross-hub write refused: 2 field(s) changed on the owner since the writer's base "
            + "and nothing was applied — re-read and re-apply"));

    [Fact]
    public void AConflictedFieldMergeUpdate_Retries_WhileBudgetRemains()
    {
        Assert.True(MeshNodeStreamCache.ShouldRetryConflict(
            Conflict(), isOverwrite: false, attempt: 0, MeshNodeStreamCache.MaxConflictRetries));
        Assert.True(MeshNodeStreamCache.ShouldRetryConflict(
            Conflict(), isOverwrite: false, attempt: MeshNodeStreamCache.MaxConflictRetries - 1,
            MeshNodeStreamCache.MaxConflictRetries));
    }

    [Fact]
    public void TheBudgetIsABound_NotASuggestion()
    {
        // At the bound the conflict SURFACES — an unbounded retry against a genuinely contested
        // node would be a write loop wearing a remedy's clothing.
        Assert.False(MeshNodeStreamCache.ShouldRetryConflict(
            Conflict(), isOverwrite: false, attempt: MeshNodeStreamCache.MaxConflictRetries,
            MeshNodeStreamCache.MaxConflictRetries));
    }

    [Fact]
    public void AnOverwrite_NeverRetries()
    {
        // An Overwrite carries no base — its conflict is not "your base went stale", and
        // re-sending the same full node would stomp the intervening write on purpose.
        Assert.False(MeshNodeStreamCache.ShouldRetryConflict(
            Conflict(), isOverwrite: true, attempt: 0, MeshNodeStreamCache.MaxConflictRetries));
    }

    [Fact]
    public void OnlyAConflictNack_Retries()
    {
        // Any other failure keeps its meaning: NotFound opens the storm-breaker, Unauthorized is
        // a denial, a transport fault is a fault. None of them prescribed a re-apply.
        Assert.False(MeshNodeStreamCache.ShouldRetryConflict(
            new MeshNodeStreamException(new MeshNodeError(
                MeshNodeErrorCode.NotFound, "X", "no such node")),
            isOverwrite: false, attempt: 0, MeshNodeStreamCache.MaxConflictRetries));
        Assert.False(MeshNodeStreamCache.ShouldRetryConflict(
            new TimeoutException("owner never answered"),
            isOverwrite: false, attempt: 0, MeshNodeStreamCache.MaxConflictRetries));
    }
}
