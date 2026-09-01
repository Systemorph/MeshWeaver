using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #2264 — <c>InstallCompileWatcher</c>'s <c>SettleAsError</c> local function is the ONLY
/// way a NodeType reaches <see cref="CompilationStatus.Error"/> under the adopt-only gate
/// (<c>Modules:RequirePrebuilt</c>, #2193 §A), which never runs a compile — so it is the only
/// writer of <see cref="NodeTypeDefinition.FailedBuildInputs"/> for such a type, and its body was
/// extracted to <see cref="NodeTypeCompilationHelpers.ApplyGateSettle"/> so the two call sites'
/// stamping decision is pinned here without a hub.
///
/// <para>Before the fix, NEITHER call site stamped <c>FailedBuildInputs</c> at all, so
/// <see cref="NodeTypeCompilationHelpers.HasStaleFailureVerdict"/> always read a require-prebuilt
/// refusal as "never attempted under these inputs" and drove one needless automatic re-drive
/// (#1793) per refused type — a second trip through the gate, a second refusal, for nothing.</para>
/// </summary>
public class GateSettleFailedBuildInputsTest
{
    private static NodeTypeDefinition Pending(
        IReadOnlyDictionary<string, long>? currentSources = null) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Pending,
        CurrentSourceVersions = currentSources,
    };

    private static ImmutableDictionary<string, long> Sources(params (string Path, long Ticks)[] entries)
    {
        var map = ImmutableDictionary<string, long>.Empty;
        foreach (var (path, ticks) in entries)
            map = map.SetItem(path, ticks);
        return map;
    }

    /// <summary>The adopt-only gate's own refusal IS formed under the live compile inputs right
    /// now, so stamping them is honest — and is exactly what stops the failed-verdict re-drive
    /// from firing on this settle.</summary>
    [Fact]
    public void TheGateRefusal_StampsTheLiveInputs()
    {
        var sources = Sources(("P/T/Source/a", 1));
        var def = Pending(sources);

        var settled = NodeTypeCompilationHelpers.ApplyGateSettle(
            def, "Modules:RequirePrebuilt refused P/T", formedUnderLiveInputs: true, modulesHash: "mod-1");

        settled.CompilationStatus.Should().Be(CompilationStatus.Error);
        settled.CompilationError.Should().Be("Modules:RequirePrebuilt refused P/T");
        settled.FailedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken("mod-1", sources));

        // …and that stamp is exactly what makes the re-drive predicate false immediately —
        // no needless automatic re-drive for a refusal that was never a stale verdict.
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(settled, "mod-1")
            .Should().BeFalse(
                "the refusal's own stamp records the inputs it was formed under; nothing about it "
                + "is stale the instant it settles");
    }

    /// <summary>The parked short-circuit re-serves an ALREADY-SETTLED failure's verdict — it must
    /// leave whatever FailedBuildInputs the node already carries untouched, or it would mask a
    /// genuine input change since that original failure and defeat #1793's recovery path.</summary>
    [Fact]
    public void TheParkedShortCircuit_LeavesAnExistingStampUntouched()
    {
        var originalStamp = NodeTypeCompilationHelpers.BuildInputsToken(
            "mod-OLD", Sources(("P/T/Source/a", 1)));
        var def = Pending(Sources(("P/T/Source/a", 1))) with { FailedBuildInputs = originalStamp };

        var settled = NodeTypeCompilationHelpers.ApplyGateSettle(
            def, "serving cached error", formedUnderLiveInputs: false, modulesHash: "mod-NEW");

        settled.CompilationStatus.Should().Be(CompilationStatus.Error);
        settled.FailedBuildInputs.Should().Be(originalStamp,
            "re-serving a parked verdict must not overwrite the record of what the ORIGINAL "
            + "failure was formed under");
    }

    /// <summary>The parked short-circuit's re-settle of a type that predates this fix (no stamp at
    /// all) must not INVENT one either — it stays null, which is the correct "never attempted"
    /// signal for whichever real failure eventually re-drives it.</summary>
    [Fact]
    public void TheParkedShortCircuit_NeverFabricatesAStampForAnUnstampedType()
    {
        var def = Pending(Sources(("P/T/Source/a", 1))) with { FailedBuildInputs = null };

        var settled = NodeTypeCompilationHelpers.ApplyGateSettle(
            def, "serving cached error", formedUnderLiveInputs: false, modulesHash: "mod-1");

        settled.FailedBuildInputs.Should().BeNull();
    }

    /// <summary>A null reason keeps whatever CompilationError the node already carries — the
    /// parked short-circuit's own default-message fallback, unaffected by this change.</summary>
    [Fact]
    public void ANullReason_KeepsTheExistingError()
    {
        var def = Pending() with { CompilationError = "original failure message" };

        var settled = NodeTypeCompilationHelpers.ApplyGateSettle(
            def, reason: null, formedUnderLiveInputs: false, modulesHash: null);

        settled.CompilationError.Should().Be("original failure message");
    }
}
