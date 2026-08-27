using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Every image in a published set must be version-tagged from the SAME source of truth</b>
/// (#2555).
///
/// <para><b>What went wrong.</b> <c>main-cd.yml</c>'s portal leg computes <c>$(Version)</c> from a
/// project in THIS repo, so it publishes <c>memex-portal-ai:3.0.0-rc8.ci.&lt;n&gt;</c>. The migration
/// leg read <c>plugins-repo/src/Memex.Database.Migration</c> — a project in <b>MeshWeaver.Plugins</b>,
/// which does not inherit this repo's root <c>Directory.Build.props</c>. It therefore evaluated to
/// the csproj's own default and published <c>memex-migration:1.0.0</c>, run after run, while a
/// comment three lines above asserted the two tags were "identical across the two legs by
/// construction".</para>
///
/// <para><b>Why it is a correctness problem.</b> <c>helm-release.yml</c> derives the migration image
/// tag from the PORTAL image's tag, on the sound reasoning that a schema migration must ride the
/// same build as the code that will run against it. With no matching version tag on
/// <c>memex-migration</c>, every <c>helm upgrade</c> minted a Job pinned to a tag that had never
/// existed. On 2026-08-27 that Job sat in <c>ImagePullBackOff</c> for 6 h 27 m — 1699 pull attempts —
/// and nothing reported it, because <c>DbVersionGate</c> only refuses to serve when
/// <c>db_version</c> is BELOW <c>DbVersion.Latest</c>. A migration that can never run is invisible
/// until the deploy that needs it.</para>
///
/// <para><b>Why a text guard.</b> The invariant lives in a workflow file and is about which
/// repository a path points into — there is no assembly to reflect over, and the failure is
/// silent by construction: both legs succeed, both push, and only the pair of tags is wrong.</para>
/// </summary>
public class CdImageVersionsShareOneSourceGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";

    /// <summary>
    /// Every <c>-getProperty:Version</c> in CD must read a project that lives in THIS repository.
    /// Any local project works — they all inherit the root <c>Directory.Build.props</c>, so they
    /// resolve one value per run — but a path into a sibling checkout resolves that repository's
    /// own scheme instead, which is exactly how the published set stopped sharing a tag.
    /// </summary>
    [Fact]
    public void EveryVersionIsComputedFromThisRepository()
    {
        var offenders = VersionSources().Where(IsForeignCheckout).ToList();

        Assert.True(offenders.Count == 0,
            "main-cd.yml computes an image version from a project outside this repository:\n  "
            + string.Join("\n  ", offenders)
            + "\nA project in another checkout does not inherit this repo's root Directory.Build.props, "
            + "so it yields a different version and the published set stops sharing one tag (#2555). "
            + "Read it from a project in this repo — the portal leg's comment says which, and why.");
    }

    /// <summary>
    /// The checkout paths CD clones sibling repositories into. Named explicitly rather than
    /// inferred from "does the file exist", because this test must fail on a workflow file it can
    /// read even when the sibling checkout is absent — which is the normal state locally, and
    /// would otherwise make the guard pass for the wrong reason.
    /// </summary>
    private static bool IsForeignCheckout(string project) =>
        project.StartsWith("plugins-repo/", StringComparison.Ordinal)
        || project.StartsWith("chart-src/", StringComparison.Ordinal)
        || project.StartsWith("/", StringComparison.Ordinal)
        || project.Contains("${{", StringComparison.Ordinal);

    /// <summary>
    /// 🚨 Discovery found something. Without this, a rename of the step or a change to the
    /// <c>-getProperty</c> spelling would empty the set and turn the assertion above green while
    /// verifying nothing — the failure mode this repo hits most often.
    /// </summary>
    [Fact]
    public void TheWorkflowActuallyYieldsVersionComputations()
    {
        var sources = VersionSources();

        Assert.True(sources.Count >= 2,
            $"Expected at least the portal and migration legs to compute a version; found {sources.Count}. "
            + "Either the steps were renamed or the matcher no longer recognises them — in both cases "
            + "this guard checks nothing.");
    }

    /// <summary>
    /// Every project path handed to <c>-getProperty:Version</c>, normalised and with any
    /// <c>${{ }}</c> expression left intact so an interpolated path is visible rather than silently
    /// rewritten into something that looks local.
    /// </summary>
    private static IReadOnlyList<string> VersionSources()
    {
        var body = File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow));

        // `dotnet msbuild <project> \` … `-getProperty:Version` — the project is the first argument,
        // and the flag may sit on a continuation line, so the two are matched together.
        var matches = Regex.Matches(
            body,
            @"dotnet\s+msbuild\s+(?<project>[^\s\\]+)(?<tail>(?:[^\n]|\\\s*\n)*?-getProperty:Version)",
            RegexOptions.Singleline);

        return [.. matches.Select(m => m.Groups["project"].Value.Replace('\\', '/'))];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root (no MeshWeaver.slnx above the test binary).");
    }
}
