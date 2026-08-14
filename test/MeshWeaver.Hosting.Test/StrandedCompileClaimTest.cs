using System;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #1462 — <c>Compiling</c> is a NON-TERMINAL state that nothing reconciles.
///
/// <para>The flip to <c>Compiling</c> is durable; the terminal write is not guaranteed. A process
/// death mid-compile therefore leaves the row there permanently, and the prewarmer's
/// "don't clobber an in-flight compile" guard then declines to touch it on every subsequent sweep.
/// The type never reaches a terminal state: neither <c>Ok</c> (usable) nor <c>Error</c>
/// (classifiable), it holds portal readiness, and it parks every instance hub for the full
/// activation budget. One row on <c>public.mesh_nodes</c> sat at <c>Compiling</c> for TEN WEEKS —
/// harmless only because it was an orphan the prewarmer never enumerates.</para>
///
/// <para>🚨 What the fix is NOT: a timer, a poller, or a background sweep for stale rows — the issue
/// rules those out explicitly, and so does AGENTS.md. The recovery rides the enumeration that
/// ALREADY runs, and only ever reinterprets a claim that has provably expired. These cases pin that
/// distinction, which is the whole substance of the change.</para>
/// </summary>
public class StrandedCompileClaimTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    private static NodeTypeDefinition Def(CompilationStatus status, DateTimeOffset? startedAt = null) =>
        new() { Configuration = "config => config", CompilationStatus = status, LastCompileStartedAt = startedAt };

    [Fact]
    public void APendingClaimIsAlwaysHonoured()
    {
        // Pending is what the prewarmer itself writes to ASK for a compile, and a driver picks it up
        // promptly — re-driving it would fight our own request.
        DynamicTypePreWarmer.IsLiveCompileClaim(Def(CompilationStatus.Pending), Budget)
            .Should().BeTrue("Pending is this prewarmer's own request, not a stranded claim");
    }

    [Fact]
    public void ACompilingClaimInsideTheBudgetIsHonoured()
    {
        var justStarted = Def(CompilationStatus.Compiling, DateTimeOffset.UtcNow.AddSeconds(-5));

        DynamicTypePreWarmer.IsLiveCompileClaim(justStarted, Budget)
            .Should().BeTrue("a compile that started 5 s ago can still be in flight — clobbering it "
                             + "would restart work that is about to finish");
    }

    [Fact]
    public void ACompilingClaimOlderThanTheBudgetIsStranded()
    {
        var stale = Def(CompilationStatus.Compiling, DateTimeOffset.UtcNow - Budget - TimeSpan.FromMinutes(1));

        DynamicTypePreWarmer.IsLiveCompileClaim(stale, Budget)
            .Should().BeFalse("past the budget no driver is still working on it — it either finished "
                              + "(and would have written a terminal state) or it died, and deferring "
                              + "to it forever is what made this permanent");
    }

    [Fact]
    public void ACompilingClaimWithNoStartTimestampIsStranded()
    {
        // A live compile always stamps LastCompileStartedAt. An unstamped Compiling is exactly the
        // shape a row left over from an older write carries — the ten-week row's shape.
        DynamicTypePreWarmer.IsLiveCompileClaim(Def(CompilationStatus.Compiling), Budget)
            .Should().BeFalse("an unstamped Compiling cannot be shown to be in flight, and honouring "
                              + "it forever is precisely the defect");
    }

    [Theory]
    [InlineData(CompilationStatus.Ok)]
    [InlineData(CompilationStatus.Error)]
    [InlineData(CompilationStatus.Unavailable)]
    [InlineData(CompilationStatus.Unknown)]
    public void ATerminalOrUnclaimedStatusIsNotAClaim(CompilationStatus status)
    {
        DynamicTypePreWarmer.IsLiveCompileClaim(Def(status), Budget)
            .Should().BeFalse("only Pending and a fresh Compiling are claims to defer to");
    }
}
