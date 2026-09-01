using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the ONE rule that decides which repository can break which:
/// <b>the platform never depends on a plugin repository.</b>
///
/// <para><c>Systemorph/MeshWeaver</c> is the platform; <c>MeshWeaver.Plugins</c> and the four
/// satellites consume it. Plugins→core is CORRECT and is not measured here — <c>$(MeshWeaverRoot)</c>
/// project references, the digest-pinned platform image the in-mesh gates compile against, and a
/// plugin repo <i>calling</i> core's reusable <c>node-repo-*.yml</c> / <c>plugin-build.yml</c>
/// workflows are all the intended shape. Only the reverse direction is a defect, and this guard
/// pins the edges that exist so a NEW one fails the pull request that adds it rather than being
/// discovered later, from a red run in somebody else's repository.</para>
///
/// <para>🚨 <b>Why a ledger rather than a ban.</b> Three edges cannot be removed by editing this
/// repo: <c>Memex.Portal.Distributed</c> (the portal host) and <c>Memex.Database.Migration</c>
/// (the migration worker) LIVE in MeshWeaver.Plugins since #2293, so the image lanes must check
/// that repo out or they have no project to publish. Those lanes are enumerated below and are
/// allowed. Every other workflow — <b>and above all the pull-request gate</b> — must reach into no
/// plugin repository at all. See Doc/Architecture/RepositoryDependencyDirection for the measured
/// inventory and the honest seams that would retire the remaining edges.</para>
///
/// <para>The scan is deliberately narrow: only the ACTIONABLE forms count — a checkout
/// (<c>repository:</c>), a reusable workflow pointed at plugin content
/// (<c>content-repository:</c>), a clone URL, or a build reading the checked-out tree
/// (<c>plugins-repo</c>). A repository NAME in prose, in an error message, or in the shared-rule
/// fleet register is not a dependency, and counting it would make this guard fire on documentation
/// — the fastest way to teach people to allow-list past it.</para>
/// </summary>
public class PlatformNeverDependsOnPluginsGuard
{
    private const string WorkflowDir = ".github/workflows";
    private const string PullRequestGate = "dotnet-test.yml";

    /// <summary>
    /// The workflows allowed to reach into a plugin repository, and why. Each entry is a lane that
    /// publishes an IMAGE whose source lives there; nothing else may join this list without moving
    /// the source, or moving the lane.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> Ledger =
        ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
        [
            new KeyValuePair<string, string>("main-cd.yml",
                "publishes portal-ai + memex-migration from plugins-repo/src, keys the image set on the "
                + "core/plugins PAIR (#2622), and packs+bakes the Plugins module bundles for that identity"),
            new KeyValuePair<string, string>("release-images.yml",
                "the v*.*.* tag lane — same two projects"),
            new KeyValuePair<string, string>("edge-images.yml",
                "the manual edge channel — same two projects"),
        ]);

    /// <summary>The repos this guard treats as "not the platform".</summary>
    private static readonly ImmutableArray<string> SatelliteRepos =
    [
        "MeshWeaver.Plugins",
        "MeshWeaver.Education",
        "MeshWeaver.Reinsurance",
        "MeshWeaver.SocialMedia",
        "MeshWeaver.Manufacturing",
    ];

    /// <summary>
    /// <b>The pull-request gate reaches into NO plugin repository.</b>
    ///
    /// <para>This is the half of the rule that costs the most when it is broken and shows the
    /// least: a gate on core's own pull requests whose verdict depends on another repository's
    /// moving HEAD makes the SAME diff go red or green with no change of its own, and blocks core
    /// merges on someone else's mistake. <c>dotnet-test.yml</c> reached this state on purpose —
    /// the plugin gates that used to check out MeshWeaver.Plugins were removed, taking the last
    /// external input with them (which is why that workflow deliberately has no preflight job).
    /// Re-adding a checkout silently undoes both.</para>
    /// </summary>
    [Fact]
    public void ThePullRequestGate_ReachesIntoNoPluginRepository()
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(), WorkflowDir, PullRequestGate);
        File.Exists(path).Should().BeTrue(
            $"{WorkflowDir}/{PullRequestGate} is the repo's pull-request gate — if it was renamed, "
            + "this guard is measuring nothing and the constant must be updated with it");

        var hits = ActionableHits(File.ReadAllText(path));

        hits.Should().BeEmpty(
            $"{PullRequestGate} is core's OWN pull-request gate: it must be decidable from this "
            + "repository alone. A cross-repo checkout here makes a core verdict depend on a "
            + "sibling's moving HEAD and reintroduces the external input the workflow was cleared "
            + "of. If a gate genuinely needs a plugin assembly, the gate belongs in the repo that "
            + "has it (see Doc/Architecture/RepositoryDependencyDirection). Found:\n{0}",
            string.Join("\n", hits));
    }

    /// <summary>
    /// <b>Exactly the image lanes reach into a plugin repository — no more, and no fewer.</b>
    ///
    /// <para>Both directions are asserted. A NEW workflow that checks out a plugin repo fails,
    /// naming itself. And a LEDGER entry that no longer matches also fails — because that is what
    /// a rename or a moved step looks like, and a guard whose pattern silently stops matching
    /// reports a clean tree forever after.</para>
    /// </summary>
    [Fact]
    public void OnlyTheImageLanes_ReachIntoAPluginRepository()
    {
        var dir = Path.Combine(SourceScan.FindRepoRoot(), WorkflowDir);
        Directory.Exists(dir).Should().BeTrue($"{WorkflowDir} must exist for this guard to measure anything");

        var files = Directory.GetFiles(dir, "*.yml").OrderBy(f => f, StringComparer.Ordinal).ToImmutableArray();
        files.Should().NotBeEmpty("an empty workflow directory would make this guard pass having read nothing");

        var reaching = files
            .Select(f => (Name: Path.GetFileName(f), Hits: ActionableHits(File.ReadAllText(f))))
            .Where(x => x.Hits.Length > 0)
            .ToImmutableArray();

        var unexpected = reaching.Where(x => !Ledger.ContainsKey(x.Name)).ToImmutableArray();
        unexpected.Should().BeEmpty(
            "the platform must not depend on a plugin repository. These workflows check one out, "
            + "clone it, or build from its tree, and are not in the ledger — a new inverted edge is "
            + "a defect, not a detail. Either remove it, or move the source so the lane needs no "
            + "sibling (Doc/Architecture/RepositoryDependencyDirection lists the seams). Found:\n{0}",
            string.Join("\n", unexpected.Select(x => $"{x.Name}:\n  " + string.Join("\n  ", x.Hits))));

        var vanished = Ledger.Keys.Where(k => reaching.All(r => !string.Equals(r.Name, k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToImmutableArray();
        vanished.Should().BeEmpty(
            "every ledger entry must still MATCH. An entry that stopped matching means either the "
            + "edge is gone — delete the entry in the same change, and say so — or the detector "
            + "stopped seeing it, which is this guard reporting a clean tree while blind. "
            + "Not matching:\n{0}",
            string.Join("\n", vanished.Select(k => $"{k} — ledgered because: {Ledger[k]}")));
    }

    /// <summary>
    /// <b>No project in this repository builds from a plugin checkout.</b>
    ///
    /// <para>The workflow half above is about CI; this is about the tree. A
    /// <c>ProjectReference</c>, <c>Import</c> or <c>Content</c> glob reaching into a sibling
    /// checkout would make `dotnet build` itself require MeshWeaver.Plugins on disk — the exact
    /// inversion of <c>$(MeshWeaverRoot)</c>, which is how the PLUGIN repo reaches core.</para>
    /// </summary>
    [Fact]
    public void NoCoreProject_BuildsFromAPluginCheckout()
    {
        var root = SourceScan.FindRepoRoot();
        var roots = new[] { "src", "test", "tools", "memex", "samples" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .ToImmutableArray();
        roots.Should().NotBeEmpty("none of the msbuild roots exist — the scan is pointed at the wrong tree");

        var files = roots
            .SelectMany(r => new[] { "*.csproj", "*.props", "*.targets" }
                .SelectMany(p => Directory.EnumerateFiles(r, p, SearchOption.AllDirectories)))
            .Where(f => !SourceScan.IsExcluded(root, f))
            .ToImmutableArray();
        files.Should().NotBeEmpty("no msbuild files found — the scan is pointed at the wrong tree");

        var offenders = files
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (Line: line, No: i + 1))
                .Where(x => SatelliteRepos.Any(r =>
                                x.Line.Contains($"{r}/", StringComparison.OrdinalIgnoreCase)
                                || x.Line.Contains($"{r}\\", StringComparison.OrdinalIgnoreCase))
                            && (x.Line.Contains("Include=", StringComparison.OrdinalIgnoreCase)
                                || x.Line.Contains("Project=", StringComparison.OrdinalIgnoreCase)))
                .Select(x => $"{SourceScan.Relative(root, f)}:{x.No}: {x.Line.Trim()}"))
            .ToImmutableArray();

        offenders.Should().BeEmpty(
            "a core msbuild file must never reference a path inside a plugin repository — that "
            + "makes `dotnet build` require a sibling checkout and inverts $(MeshWeaverRoot). "
            + "Found:\n{0}",
            string.Join("\n", offenders));
    }

    /// <summary>
    /// <b>Negative control.</b> Every assertion above is "found nothing", so the detector's silence
    /// must be shown to be a verdict rather than a bug. Each actionable form is fed to the scanner
    /// and must be reported; prose naming the same repository must NOT be.
    /// </summary>
    [Fact]
    public void TheDetector_FiresOnEachActionableForm_AndIsSilentOnProse()
    {
        ActionableHits("      - uses: actions/checkout@v7\n        with:\n          repository: Systemorph/MeshWeaver.Plugins\n")
            .Should().NotBeEmpty("a `repository:` checkout of the plugin repo is the primary form");

        ActionableHits("    with:\n      content-repository: Systemorph/MeshWeaver.Education\n")
            .Should().NotBeEmpty("a reusable workflow pointed at satellite content is the same edge by another name");

        ActionableHits("          git ls-remote git@github.com:Systemorph/MeshWeaver.Plugins.git main\n")
            .Should().NotBeEmpty("a clone URL reaches the repo without an actions/checkout");

        ActionableHits("          dotnet publish plugins-repo/src/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj\n")
            .Should().NotBeEmpty("building out of the checked-out tree is the point of the checkout");

        ActionableHits("# The portal HOST lives in MeshWeaver.Plugins (#2293 removed it from here).\n")
            .Should().BeEmpty("a comment naming the repo is documentation, not a dependency");

        ActionableHits("        echo \"the GUI clients moved to MeshWeaver.Plugins, MeshWeaver#2169\"\n")
            .Should().BeEmpty("the repository NAME in an error string is not a dependency");

        ActionableHits("          repositories: >-\n            MeshWeaver,MeshWeaver.Plugins,MeshWeaver.Education\n")
            .Should().BeEmpty(
                "the shared-rule fleet register names every repo as DATA for an App token; it "
                + "checks nothing out (that gate's own coupling is documented, not detected here)");
    }

    // ── the detector ──────────────────────────────────────────────────────────────────────
    //
    // Comment lines are masked FIRST: this repo's workflows explain their history in prose, and a
    // scanner that counted a comment would fire on documentation of the very edge it measures.

    private static readonly Regex CommentLine = new(@"^\s*#", RegexOptions.Compiled);

    private static ImmutableArray<string> ActionableHits(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var hits = ImmutableArray.CreateBuilder<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (CommentLine.IsMatch(line))
                continue;

            var actionable =
                // an actions/checkout, or a reusable workflow pointed at that repo's content
                SatelliteRepos.Any(r => Regex.IsMatch(line, $@"(^|\s)(content-)?repository:\s*Systemorph/{Regex.Escape(r)}\b"))
                // a clone URL — reaches the repo with no actions/checkout at all
                || SatelliteRepos.Any(r => line.Contains($"Systemorph/{r}.git", StringComparison.OrdinalIgnoreCase))
                // …and anything that reads the checked-out tree
                || Regex.IsMatch(line, @"(^|[\s""'=/])plugins-repo([/\s""']|$)");

            if (actionable)
                hits.Add($"line {i + 1}: {line.Trim()}");
        }

        return hits.ToImmutable();
    }
}
