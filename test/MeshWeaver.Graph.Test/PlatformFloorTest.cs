using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins THE floor decision — <see cref="PlatformFloor.Evaluate"/>, policy
/// <c>package-min-mesh-version</c>: a package version declaring <c>minMeshVersion</c> is used only
/// when the running platform satisfies it, and only a COMPARABLE floor strictly ABOVE the running
/// version holds.
///
/// <para>Two incidents bound it from either side, and each has its own named test so a change that
/// re-opens one cannot pass by closing the other:</para>
/// <list type="bullet">
/// <item><b>2026-09-07 — a floor that could never be met.</b> SemVer ranks <c>ci &lt; rc &lt; clean</c>,
/// so every <c>rc</c> or clean floor held every <c>ci</c> portal on its morning build.
/// <see cref="TheSeptember7Trap_NeverHolds"/>.</item>
/// <item><b>2026-09-27 — a floor that was never read.</b> Store 1.16 and Hosting declared newer
/// platform members and synced onto <c>3.0.0-ci.9412</c>/<c>9414</c> anyway; 14 NodeTypes lost
/// their assembly. <see cref="TheSeptember27Shape_Holds"/>.</item>
/// </list>
/// </summary>
public class PlatformFloorTest
{
    /// <summary>
    /// 🚨 <b>The 2026-09-07 trap, as a named test.</b> None of these may ever hold: an rc floor and a
    /// clean floor against a ci build (SemVer would say "unmet" for every run number), an ancient
    /// floor, an unreadable floor, no floor, and a LOCAL source build (<c>-ci.0</c>) on either side.
    /// Every one of them proceeds — as <see cref="PlatformFloorKind.Satisfied"/>,
    /// <see cref="PlatformFloorKind.None"/> or <see cref="PlatformFloorKind.Advisory"/>, never
    /// <see cref="PlatformFloorKind.Held"/>.
    /// </summary>
    [Theory]
    [InlineData("3.0.0-rc8", "3.0.0-ci.8055")]
    [InlineData("3.0.0-rc8", "3.0.0-ci.1")]
    [InlineData("3.0.0-rc8", "3.0.0-ci.999999999")]
    [InlineData("3.0.0-rc8", "3.0.0-rc9.ci.7693")]
    [InlineData("3.0.0", "3.0.0-ci.9412")]
    [InlineData("3.0.0", "3.0.0-ci.1")]
    [InlineData("1.0.0", "3.0.0-ci.9412")]
    [InlineData("1.0.0", "3.0.0")]
    [InlineData("not-a-version", "3.0.0-ci.9412")]
    [InlineData("latest", "3.0.0-ci.9412")]
    [InlineData("", "3.0.0-ci.9412")]
    [InlineData(null, "3.0.0-ci.9412")]
    [InlineData("3.0.0-ci.9494", "3.0.0-ci.0")]
    [InlineData("999.0.0", "3.0.0-ci.0")]
    [InlineData("3.0.0-ci.0", "3.0.0-ci.9412")]
    [InlineData("3.0.0-ci.9494", null)]
    [InlineData("3.0.0-ci.9494", "unknown")]
    [InlineData("3.0.0-ci.9494", "3.0.0")]
    public void TheSeptember7Trap_NeverHolds(string? floor, string? running)
    {
        var verdict = PlatformFloor.Evaluate(floor, running);

        Assert.False(verdict.IsHeld,
            $"floor '{floor}' against running '{running}' was HELD ({verdict.Reason}). This is the "
            + "2026-09-07 shape: a comparison that cannot be ordered, or SemVer's ci < rc < clean, "
            + "must never hold a portal.");
        Assert.NotEqual(PlatformFloorKind.Held, verdict.Kind);
        Assert.Null(PlatformFloor.HoldReason(floor, running));
    }

    /// <summary>The kinds are not interchangeable: an unorderable comparison says ADVISORY — with a
    /// sentence naming both versions — never a silent "satisfied".</summary>
    [Theory]
    [InlineData("3.0.0-rc8", "3.0.0-ci.8055")]
    [InlineData("not-a-version", "3.0.0-ci.9412")]
    [InlineData("3.0.0-ci.9494", "3.0.0-ci.0")]
    [InlineData("3.0.0-ci.9494", null)]
    [InlineData("3.0.0-ci.9494", "3.0.0")]
    public void AnUnorderableComparison_IsAdvisory_AndSaysSo(string floor, string? running)
    {
        var verdict = PlatformFloor.Evaluate(floor, running);

        Assert.Equal(PlatformFloorKind.Advisory, verdict.Kind);
        Assert.NotNull(verdict.Reason);
        Assert.Contains(floor, verdict.Reason);
        Assert.Contains("advisory", verdict.Reason);
    }

    /// <summary>
    /// 🚨 <b>The 2026-09-27 shape — the floor this policy exists to honour.</b> Two continuous builds:
    /// the run number decides. <c>3.0.0-ci.N</c> against <c>3.0.0-ci.M</c> holds iff N &gt; M.
    /// </summary>
    [Theory]
    [InlineData("3.0.0-ci.9494", "3.0.0-ci.9412", true)]
    [InlineData("3.0.0-ci.9494", "3.0.0-ci.9414", true)]
    [InlineData("3.0.0-ci.9413", "3.0.0-ci.9412", true)]
    [InlineData("3.0.0-ci.9412", "3.0.0-ci.9412", false)]
    [InlineData("3.0.0-ci.9411", "3.0.0-ci.9412", false)]
    [InlineData("3.0.0-ci.7845", "3.0.0-ci.9412", false)]
    // The mislabelled line of 2026-09-05 loses on the run number, in either role.
    [InlineData("3.1.0-ci.7841", "3.0.0-ci.9412", false)]
    [InlineData("3.0.0-ci.9494", "3.1.0-ci.7841", true)]
    public void TheSeptember27Shape_Holds(string floor, string running, bool held)
    {
        var verdict = PlatformFloor.Evaluate(floor, running);

        Assert.Equal(held, verdict.IsHeld);
        Assert.Equal(held ? PlatformFloorKind.Held : PlatformFloorKind.Satisfied, verdict.Kind);
        if (held)
        {
            Assert.Contains(floor, verdict.Reason);
            Assert.Contains(running, verdict.Reason);
            Assert.Equal(verdict.Reason, PlatformFloor.HoldReason(floor, running));
        }
    }

    /// <summary>Where one side is a release and run numbers cannot be compared, the NUMERIC core
    /// decides: a strictly higher core holds, a lower core is satisfied, and an equal core with a
    /// clean floor is satisfied by every build of its line (the rule
    /// <c>Doc/Architecture/SelfUpdateTargetSelection</c> §4 states for a <c>3.0.0</c> floor).</summary>
    [Theory]
    [InlineData("3.1.0", "3.0.0-ci.9412", true)]
    [InlineData("999.0.0", "3.0.0-ci.9412", true)]
    [InlineData("999.0.0", "3.0.0", true)]
    [InlineData("4.0.0", "3.9.9", true)]
    [InlineData("3.0.0", "3.0.0", false)]
    [InlineData("2.9.0", "3.0.0-ci.9412", false)]
    [InlineData("3.0.0", "3.1.0-ci.7841", false)]
    public void AcrossTheBands_TheNumericCoreDecides(string floor, string running, bool held)
        => Assert.Equal(held, PlatformFloor.Evaluate(floor, running).IsHeld);

    /// <summary>No floor is no constraint — and not an advisory either.</summary>
    [Fact]
    public void NoFloor_IsNone()
    {
        Assert.Equal(PlatformFloorKind.None, PlatformFloor.Evaluate(null, "3.0.0-ci.1").Kind);
        Assert.Equal(PlatformFloorKind.None, PlatformFloor.Evaluate("  ", "3.0.0-ci.1").Kind);
        Assert.Null(PlatformFloor.Evaluate(null, "3.0.0-ci.1").Reason);
    }

    /// <summary>The ONE running-version reader strips build metadata and reads "unknown" as no
    /// version — so a floor against an unstamped build is advisory, never held.</summary>
    [Theory]
    [InlineData("3.0.0-ci.9412+abc123", "3.0.0-ci.9412")]
    [InlineData("3.0.0+0a1eabdc", "3.0.0")]
    [InlineData("3.0.0-ci.9412", "3.0.0-ci.9412")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheRunningVersion_IsStrippedOfBuildMetadata(string? reported, string? expected)
        => Assert.Equal(expected, PlatformBuildInfo.StripBuildMetadata(reported));
}
