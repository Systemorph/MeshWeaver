using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh;

/// <summary>One module as a test run sees it: installed vs the target set's version.</summary>
/// <param name="Id">The module (package) id.</param>
/// <param name="Installed">The installed version (SemVer, else content hash), or null.</param>
/// <param name="Target">The target set's version, or null when the target names none.</param>
/// <param name="Held">Why a newer version is not installed, when a hold says so.</param>
/// <param name="Behind">Whether the module is behind its target, as the reader DECIDED it (by
/// content hash, a policy a person keeps, …). Null when the reader decided nothing, in which case
/// the two version strings are compared.</param>
public sealed record ModuleUnderTest(string Id, string? Installed, string? Target, string? Held = null, bool? Behind = null)
{
    /// <summary>Whether the module counts as at its target. The reader's own decision wins, so a
    /// re-published version with new content (same SemVer, new hash) cannot read as at target; with
    /// no decision, a module with no target is never behind — there is nothing to measure it
    /// against.</summary>
    public bool AtTarget => Behind is { } behind
        ? !behind
        : Target is null || string.Equals(Installed, Target, StringComparison.Ordinal);

    /// <summary>Whether the header should name a target: there is one, and it is not what runs.</summary>
    public bool ShowsTarget => Target is not null && !string.Equals(Installed, Target, StringComparison.Ordinal);
}

/// <summary>
/// 🚨 <b>What a test run TESTED</b> — recorded as the run's header so every verdict names exactly
/// the platform and the module versions it ran against (maintainer, 2026-10-04: "we must know which
/// version we want to test"; "should be always beginning of each test run", "done by framework").
/// </summary>
/// <param name="Platform">The running platform version.</param>
/// <param name="Commit">The core commit the platform was built from.</param>
/// <param name="TargetPlatform">The target set's platform, or null when no target is known.</param>
/// <param name="Modules">The installed modules, with their target versions.</param>
/// <param name="Source">Where the reading came from (an instance id, or "in-process mesh").</param>
public sealed record VersionsUnderTest(
    string? Platform,
    string? Commit,
    string? TargetPlatform,
    ImmutableList<ModuleUnderTest> Modules,
    string Source)
{
    /// <summary>The run header — one line naming the platform and target, then one per module that
    /// differs from the target (all modules when <paramref name="all"/>). Pure.</summary>
    /// <param name="all">List every module, not only those off-target.</param>
    public string Header(bool all = true)
    {
        if (Unread)
            return $"Versions under test — {Source}: platform, target and modules NOT READ (versions unknown).";
        var commit = Commit is { Length: > 0 } sha && !string.IsNullOrWhiteSpace(sha) ? $" (core {Short(sha)})" : "";
        var target = string.IsNullOrWhiteSpace(TargetPlatform) ? "no target known" : $"target {TargetPlatform}";
        var lines = new List<string>
        {
            $"Versions under test — {Source}: platform {Platform ?? "(unknown)"}{commit}; {target}; "
            + $"{Modules.Count} module(s), {Modules.Count(m => !m.AtTarget)} off-target.",
        };
        lines.AddRange(Modules
            .Where(m => all || !m.AtTarget)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"  {m.Id} {m.Installed ?? "(unknown)"}"
                + (m.ShowsTarget ? $" → target {m.Target}" : "")
                + (string.IsNullOrWhiteSpace(m.Held) ? "" : $" [{m.Held}]")));
        return string.Join("\n", lines);
    }

    /// <summary>True when the instance's own reading failed, so every other member is a placeholder
    /// and the header must say the versions are unknown rather than print them.</summary>
    public bool Unread { get; init; }

    /// <summary>The reading for an instance whose versions could not be read — names no platform,
    /// no commit and no modules, because none are known.</summary>
    /// <param name="source">Where the failed reading was attempted.</param>
    public static VersionsUnderTest NotRead(string source) =>
        new(null, null, null, ImmutableList<ModuleUnderTest>.Empty, source) { Unread = true };

    private static string Short(string sha) => sha.Length <= 9 ? sha : sha[..9];

    /// <summary>
    /// This PROCESS — what an in-process test mesh runs: the platform build it was compiled as and
    /// the core commit. No target: an in-process mesh IS what it tests, so it has nothing to
    /// converge to.
    /// </summary>
    public static VersionsUnderTest OfThisProcess() =>
        new(PlatformBuildInfo.RunningPlatformVersion ?? PlatformBuildInfo.PlatformVersion,
            PlatformBuildInfo.CommitHash,
            TargetPlatform: null,
            Modules: ImmutableList<ModuleUnderTest>.Empty,
            Source: "in-process mesh");
}

/// <summary>What the preflight decided.</summary>
public enum TestRunPreflightKind
{
    /// <summary>At the target (or no target known): the run proceeds unchanged.</summary>
    UpToDate = 0,

    /// <summary>Behind, and the run may change the instance: converge first, then re-read.</summary>
    Converge = 1,

    /// <summary>Behind, and the run may NOT change it (or converging did not close the gap): FAIL
    /// fast, naming the skew — never silently test an old version.</summary>
    Skew = 2,
}

/// <summary>The preflight verdict and the reading it was taken from.</summary>
/// <param name="Kind">The decision.</param>
/// <param name="Versions">What is (or, after converging, was re-read as) under test.</param>
/// <param name="Message">The sentence a run prints — for a skew, every gap by name.</param>
public sealed record TestRunPreflightResult(TestRunPreflightKind Kind, VersionsUnderTest Versions, string Message)
{
    /// <summary>Whether the run may proceed.</summary>
    public bool Proceed => Kind != TestRunPreflightKind.Skew;
}

/// <summary>
/// 🚨 <b>THE FIRST STEP OF EVERY TEST RUN THAT TARGETS A MESH</b> (maintainer, 2026-10-04) — the
/// framework's, never a test's: (1) resolve the target set, (2) read what the mesh runs, (3)
/// converge it, or FAIL fast naming the skew on a mesh the run may not change, (4) record the
/// versions under test as the run's header. Called by <c>MeshOperations.RunTests</c> (every in-mesh
/// Tests-area run, the CI gate's and an agent's alike) and by the monolith test base. A host that
/// registers no implementation tests an in-process mesh — <see cref="TestRunPreflight.Default"/> — which is its own
/// target and never needs converging.
/// </summary>
public interface ITestRunPreflight
{
    /// <summary>Reads, converges when <paramref name="mayConverge"/>, and decides. Cold; emits once;
    /// never faults (an unreadable target is a SKEW naming what could not be read, not a pass).</summary>
    /// <param name="mayConverge">Whether this run may change the mesh it tests.</param>
    IObservable<TestRunPreflightResult> Prepare(bool mayConverge);
}

/// <summary>The decision, pure — every arm pinnable without a mesh.</summary>
public static class TestRunPreflight
{
    /// <summary>The preflight for a host with no target-set provider: this process, up to date.</summary>
    public static ITestRunPreflight Default { get; } = new InProcess();

    /// <summary>
    /// Decides from one reading. Platform behind the target is a SKEW whatever <paramref name="mayConverge"/>
    /// says (a test run never rolls an image; the instance's CD does); modules behind are CONVERGE
    /// when the run may change the mesh and have not been converged yet, otherwise SKEW.
    /// </summary>
    /// <param name="versions">What the mesh runs.</param>
    /// <param name="platformAtTarget">Whether the running platform is at or above the target.</param>
    /// <param name="mayConverge">Whether the run may change the mesh.</param>
    /// <param name="alreadyConverged">Whether a convergence pass has already run for this reading.</param>
    public static TestRunPreflightResult Decide(
        VersionsUnderTest versions, bool platformAtTarget, bool mayConverge, bool alreadyConverged)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var behind = versions.Modules.Where(m => !m.AtTarget).ToImmutableList();
        if (platformAtTarget && behind.Count == 0)
            return new(TestRunPreflightKind.UpToDate, versions,
                string.IsNullOrWhiteSpace(versions.TargetPlatform)
                    ? $"{versions.Source}: no target set known — nothing to converge."
                    : $"{versions.Source}: at the target set ({versions.TargetPlatform}) — nothing to converge.");
        if (platformAtTarget && mayConverge && !alreadyConverged)
            return new(TestRunPreflightKind.Converge, versions,
                $"{versions.Source}: {behind.Count} module(s) behind the target set — converging before the run: "
                + string.Join(", ", behind.Select(m => $"{m.Id} {m.Installed ?? "?"} → {m.Target}")));
        var gaps = new List<string>();
        if (!platformAtTarget)
            gaps.Add($"platform {versions.Platform ?? "(unknown)"} is behind the target {versions.TargetPlatform ?? "(unknown)"} "
                + "(a test run never rolls an image — the instance's CD does)");
        gaps.AddRange(behind.Select(m => $"{m.Id} {m.Installed ?? "(unknown)"} ≠ target {m.Target}"
            + (string.IsNullOrWhiteSpace(m.Held) ? "" : $" ({m.Held})")));
        return new(TestRunPreflightKind.Skew, versions,
            $"{versions.Source} is NOT at the target set — refusing to test an old version"
            + (alreadyConverged ? " (a convergence pass ran and did not close it)" : mayConverge ? "" : " (this run may not change it)")
            + ": " + string.Join("; ", gaps));
    }

    private sealed class InProcess : ITestRunPreflight
    {
        public IObservable<TestRunPreflightResult> Prepare(bool mayConverge) =>
            Observable.Defer(() => Observable.Return(
                Decide(VersionsUnderTest.OfThisProcess(), platformAtTarget: true, mayConverge, alreadyConverged: false)));
    }
}
