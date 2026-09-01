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

    /// <summary>
    /// 🚨 <b>Every leg of a published set must be BUILT from the same plugin COMMIT</b> — the same
    /// rule as the version above, one level down: not "tagged from one source of truth" but
    /// "compiled from one tree".
    ///
    /// <para><b>What went wrong.</b> Each consumer resolved the plugins ref for itself.
    /// <c>gate</c> resolved the HEAD of <c>MW_PLUGINS_REF</c> (default <c>main</c>) ONCE, minted the
    /// image-set identity from it, and <c>promote</c> tagged the portal
    /// <c>&lt;core-sha&gt;-p&lt;plugins-sha&gt;</c> with that value — while <c>portal-image</c>,
    /// <c>migration-image</c> and the two reusable calls each checked out the BRANCH, seconds to
    /// minutes later. <c>node-repo-module-pack</c> alone checks the content repo out three times.
    /// A plugins merge landing mid-run therefore produced an image built from a commit the pair tag
    /// does not name, and — the part that matters — a portal image and the module bundles sealed
    /// beside it from DIFFERENT trees. There is no image that can serve a mixed set of module
    /// builds; that is the whole reason the pair key (#2622) exists.</para>
    ///
    /// <para><b>Why it is silent.</b> Every leg succeeds. Every image pushes. The tags are
    /// well-formed. Only the provenance they assert is false, and nothing compares a built tree to
    /// the sha that named it — exactly the shape of #2555 one layer up.</para>
    ///
    /// <para>Asserted structurally: every checkout or reusable call in CD that names a plugin
    /// repository must take its ref from <c>needs.gate.outputs.plugins_sha</c>, the value
    /// <c>gate</c> resolved once. A branch name there — <c>main</c>, or the steering variable
    /// unresolved — is the regression.</para>
    /// </summary>
    [Fact]
    public void EveryPluginCheckoutUsesTheCommitTheGateResolved()
    {
        var pins = PluginRefs();

        Assert.True(pins.Count >= 4,
            $"Expected at least the two image legs and the two reusable calls to name a plugin "
            + $"repository in {Workflow}; found {pins.Count}. Either they were renamed or the "
            + "matcher no longer recognises them — in both cases this guard checks nothing.");

        var offenders = pins.Where(x => !x.Ref.Contains("needs.gate.outputs.plugins_sha", StringComparison.Ordinal)).ToList();

        Assert.True(offenders.Count == 0,
            "main-cd.yml reaches into a plugin repository at a ref it resolved for itself:\n  "
            + string.Join("\n  ", offenders.Select(x => $"line {x.Line}: {x.Repo} @ {x.Ref}"))
            + "\nEvery leg of one run must build from ONE plugin commit — the sha `gate` already "
            + "resolved and `promote` tags the image set with. Re-resolving a branch here lets a "
            + "mid-run merge ship an image whose pair tag names a different tree, and lets the "
            + "module bundles be sealed from a third. Use ${{ needs.gate.outputs.plugins_sha }}; "
            + "MW_PLUGINS_REF keeps steering the run through `gate`, which resolves it ONCE.");
    }

    /// <summary>
    /// Every place CD names a plugin repository, paired with the ref that checkout or reusable call
    /// will use — the next <c>ref:</c>/<c>content-ref:</c> within the same <c>with:</c> block.
    /// Comment lines are skipped: this workflow explains its own history in prose, and a matcher
    /// that read a comment would report an edge that does not exist.
    /// </summary>
    private static IReadOnlyList<(int Line, string Repo, string Ref)> PluginRefs()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), Workflow));
        var found = new List<(int, string, string)>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\s*#")) continue;
            var repo = Regex.Match(lines[i], @"^\s*(?:content-)?repository:\s*(?<repo>Systemorph/MeshWeaver\.[A-Za-z.]+)\s*$");
            if (!repo.Success) continue;

            // The ref sits within the same `with:` block — a handful of lines, never a whole job.
            var refValue = "<none>";
            for (var j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
            {
                if (Regex.IsMatch(lines[j], @"^\s*#")) continue;
                var m = Regex.Match(lines[j], @"^\s*(?:content-)?ref:\s*(?<ref>.+?)\s*$");
                if (m.Success) { refValue = m.Groups["ref"].Value; break; }
            }

            found.Add((i + 1, repo.Groups["repo"].Value, refValue));
        }

        return found;
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
