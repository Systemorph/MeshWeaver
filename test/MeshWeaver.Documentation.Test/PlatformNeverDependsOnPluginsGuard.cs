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
///
/// <para>🚨 <b>Two classes, two ledgers, and the boundary between them is the point (#2689).</b>
/// CODE crossing the boundary — a checkout, a clone, a build out of the checked-out tree — is the
/// class above, and it is banned on the pull-request gate outright. A credentialed API READ is a
/// DIFFERENT class: it makes a core verdict depend on a sibling's state without letting a single
/// line of plugin source into core's build, so <c>dotnet build</c> here still needs no sibling on
/// disk. That class is <b>permitted, deliberately, and inventoried</b> — <c>shared-rules</c> has
/// read <c>AGENTS.md</c> from all seven repos since #2732, and <c>cross-repo-pair</c> resolves a
/// declared <c>Pairs-with:</c> pull request since #2689. Both sit on core's pull-request path and
/// both can block a core merge on a sibling's state. Until #2689 that class was invisible here,
/// which is the failure shape this guard names one paragraph up: a detector that stops seeing its
/// subject reports a clean tree forever after. So it is DETECTED now, with its own ledger asserted
/// in both directions — see <see cref="OnlyLedgeredWorkflows_ReadASiblingThroughTheApi"/>.</para>
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

    /// <summary>
    /// The workflows allowed to READ a plugin repository through the GitHub API, and why. This is
    /// a SECOND, deliberately separate ledger: its entries do NOT pull plugin code into core's
    /// build, so they are permitted on the pull-request path where <see cref="Ledger"/>'s class is
    /// banned outright. The cost is real and is the reason they are enumerated — each one can turn
    /// a core pull request red because of a sibling's state, so a new one is an architectural
    /// decision, not a detail. Doc/Architecture/RepositoryDependencyDirection § C is the prose.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> ApiReadLedger =
        ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
        [
            new KeyValuePair<string, string>("dotnet-test.yml",
                "`shared-rules` reads AGENTS.md from all seven repos (#2732 — a per-repo self-check "
                + "cannot detect the missed-repo case), and `cross-repo-pair` resolves the "
                + "`Pairs-with:` pull request a surface-removing change declares (#2689 — the "
                + "deleting half must land LAST). Both mint a scoped App installation token and "
                + "read; neither checks anything out"),
            new KeyValuePair<string, string>("shared-rules.yml",
                "the scheduled half of the same shared-rule sweep, so a drift is caught in a week "
                + "when nobody opens a pull request anywhere"),
            new KeyValuePair<string, string>("dependent-suites.yml",
                "the DISPATCHER of #3103: a pull request that changes the public declaration set "
                + "asks MeshWeaver.Plugins (repository_dispatch, App token scoped to that repo) to "
                + "build and run its suites against this pull request's MERGE commit, then reads the "
                + "verdict the dependent recorded in ITS OWN repository (a marker ref) and finishes "
                + "with it. It reads a fact and sends an event; it checks nothing out and writes "
                + "nothing into core — the dependent's suite runs THERE, against a candidate commit, "
                + "which is the CI-time integration Doc/Architecture/CrossRepoPairGate names as the "
                + "structural answer, never a build-time reference"),
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
    /// <b>Exactly the ledgered workflows READ a sibling repository through the API — no more, and
    /// no fewer.</b>
    ///
    /// <para>This is the class <see cref="ThePullRequestGate_ReachesIntoNoPluginRepository"/>
    /// deliberately does NOT ban, and the distinction is load-bearing rather than a loophole: a
    /// checkout puts another repository's SOURCE into core's build, while an API read puts only a
    /// FACT about it into a verdict. Core still compiles, tests and ships with no sibling on disk.
    /// That is why <c>shared-rules</c> is allowed to block a core merge on a satellite's AGENTS.md
    /// (#2732), and why <c>cross-repo-pair</c> may block one on a declared counterpart still being
    /// open (#2689) — in both cases the evidence exists nowhere else, because core's CI does not
    /// build the plugin repos at all.</para>
    ///
    /// <para>🚨 It is still a REAL edge, and the cost is the same as the other ledger's: the same
    /// diff can go red or green with no change of its own. So it is enumerated rather than
    /// tolerated, and asserted in BOTH directions — a NEW workflow that starts reading a sibling
    /// fails, naming itself, and a ledger entry that stops matching fails too, because that is
    /// what a rename or a removed step looks like and a guard whose pattern silently stops
    /// matching reports a clean tree forever after.</para>
    /// </summary>
    [Fact]
    public void OnlyLedgeredWorkflows_ReadASiblingThroughTheApi()
    {
        var dir = Path.Combine(SourceScan.FindRepoRoot(), WorkflowDir);
        var files = Directory.GetFiles(dir, "*.yml").OrderBy(f => f, StringComparer.Ordinal).ToImmutableArray();
        files.Should().NotBeEmpty("an empty workflow directory would make this guard pass having read nothing");

        var reading = files
            .Select(f => (Name: Path.GetFileName(f), Hits: CrossRepoApiHits(File.ReadAllText(f))))
            .Where(x => x.Hits.Length > 0)
            .ToImmutableArray();

        var unexpected = reading.Where(x => !ApiReadLedger.ContainsKey(x.Name)).ToImmutableArray();
        unexpected.Should().BeEmpty(
            "a workflow that reads a plugin repository through the API makes a core verdict depend "
            + "on a sibling's state. That is permitted — it is how shared-rules and cross-repo-pair "
            + "work, and the evidence they need exists nowhere else — but it is an architectural "
            + "decision, not a detail, so every instance is ledgered with its reason here and in "
            + "Doc/Architecture/RepositoryDependencyDirection. Add the entry in the same change, or "
            + "move the read to the repo that owns the subject. Found:\n{0}",
            string.Join("\n", unexpected.Select(x => $"{x.Name}:\n  " + string.Join("\n  ", x.Hits))));

        var vanished = ApiReadLedger.Keys
            .Where(k => reading.All(r => !string.Equals(r.Name, k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToImmutableArray();
        vanished.Should().BeEmpty(
            "every API-read ledger entry must still MATCH. An entry that stopped matching means "
            + "either the read is gone — delete the entry in the same change, and say so — or the "
            + "detector stopped seeing it, which is this guard reporting a clean tree while blind. "
            + "Not matching:\n{0}",
            string.Join("\n", vanished.Select(k => $"{k} — ledgered because: {ApiReadLedger[k]}")));
    }

    /// <summary>
    /// <b>Negative control for the API-read detector.</b> Same posture as
    /// <see cref="TheDetector_FiresOnEachActionableForm_AndIsSilentOnProse"/>: every form must be
    /// shown to FIRE, and the prose that names the same repositories must stay silent — otherwise
    /// the ledger above is an inventory of whatever the regex happens to match.
    /// </summary>
    [Fact]
    public void TheApiDetector_FiresOnEachReadForm_AndIsSilentOnProse()
    {
        CrossRepoApiHits(
            "        repositories: >-\n          MeshWeaver,MeshWeaver.Plugins,MeshWeaver.Education\n")
            .Should().NotBeEmpty(
                "a fleet-scoped App installation token IS the capability to read those repos — the "
                + "register is the actionable form, not the later call site, because the call site "
                + "is usually a runtime variable");

        CrossRepoApiHits("          repositories: MeshWeaver.Plugins\n")
            .Should().NotBeEmpty("…and an inline single-repo register is the same capability");

        CrossRepoApiHits(
            "          curl -H \"Authorization: Bearer $T\" https://api.github.com/repos/Systemorph/MeshWeaver.Plugins/pulls/904\n")
            .Should().NotBeEmpty("a direct API call reaches the repo whether or not a register named it");

        CrossRepoApiHits("          gh api repos/systemorph/meshweaver.plugins/pulls/904 --jq .merged\n")
            .Should().NotBeEmpty(
                "GitHub resolves owner/repo case-insensitively, so a lowercased `gh api` call is the "
                + "same read — a case-sensitive detector would go blind and report a clean tree");

        CrossRepoApiHits("# shared-rules reads AGENTS.md from MeshWeaver.Plugins and four satellites.\n")
            .Should().BeEmpty("a comment naming the repo is documentation, not a read");

        CrossRepoApiHits("        echo \"the GUI clients moved to MeshWeaver.Plugins, MeshWeaver#2169\"\n")
            .Should().BeEmpty("the repository NAME in an error string is not a read");

        CrossRepoApiHits("          repositories: ${{ steps.content-repo.outputs.name }}\n")
            .Should().BeEmpty(
                "a register whose value is an EXPRESSION names no satellite in this tree — the "
                + "node-repo-* lanes are called BY a satellite and scope the token to the caller, "
                + "which is the arrow pointing the right way");

        // 🚨 The two detectors must stay DISJOINT, or the two ledgers would double-count and each
        // would fail on the other's entries. A checkout is code crossing the boundary and belongs
        // to `Ledger`; nothing about it is an API read.
        CrossRepoApiHits("          repository: Systemorph/MeshWeaver.Plugins\n")
            .Should().BeEmpty("a CHECKOUT is the other class — it is `Ledger`'s, and ActionableHits sees it");
        ActionableHits("        repositories: >-\n          MeshWeaver,MeshWeaver.Plugins,MeshWeaver.Education\n")
            .Should().BeEmpty("…and an App-token register checks nothing out, so it is not `Ledger`'s");
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

        ActionableHits("          repository: systemorph/meshweaver.plugins\n")
            .Should().NotBeEmpty(
                "GitHub resolves owner/repo case-insensitively, so a lowercased checkout is the "
                + "same edge — a case-sensitive detector would go blind and report a clean tree");

        ActionableHits("          dotnet publish Plugins-Repo/src/Memex.Portal.Distributed/x.csproj\n")
            .Should().NotBeEmpty("…and so is a differently-cased checkout path");

        ActionableHits("# The portal HOST lives in MeshWeaver.Plugins (#2293 removed it from here).\n")
            .Should().BeEmpty("a comment naming the repo is documentation, not a dependency");

        ActionableHits("        echo \"the GUI clients moved to MeshWeaver.Plugins, MeshWeaver#2169\"\n")
            .Should().BeEmpty("the repository NAME in an error string is not a dependency");

        ActionableHits("          repositories: >-\n            MeshWeaver,MeshWeaver.Plugins,MeshWeaver.Education\n")
            .Should().BeEmpty(
                "the shared-rule fleet register names every repo as DATA for an App token; it "
                + "checks nothing out, so it is not THIS detector's class. 🚨 It is not undetected "
                + "either, which it was until #2689: CrossRepoApiHits sees it and "
                + "OnlyLedgeredWorkflows_ReadASiblingThroughTheApi ledgers it. Two classes, two "
                + "detectors, disjoint on purpose — see this file's summary");
    }

    // ── the detector ──────────────────────────────────────────────────────────────────────
    //
    // Comment lines are masked FIRST: this repo's workflows explain their history in prose, and a
    // scanner that counted a comment would fire on documentation of the very edge it measures.

    private static readonly Regex CommentLine = new(@"^\s*#", RegexOptions.Compiled);

    // 🚨 IgnoreCase throughout (Copilot review, #2965). GitHub resolves owner/repo
    // case-insensitively, so `systemorph/meshweaver.plugins` or `Plugins-Repo` is the same
    // checkout — and a case-sensitive detector would stop seeing its subject and report a clean
    // tree, which is this guard's own failure mode turned on itself. The `.git` clone-URL probe
    // below was already OrdinalIgnoreCase; the two regexes were the inconsistency.
    private const RegexOptions Loose = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

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
                SatelliteRepos.Any(r => Regex.IsMatch(line, $@"(^|\s)(content-)?repository:\s*Systemorph/{Regex.Escape(r)}\b", Loose))
                // a clone URL — reaches the repo with no actions/checkout at all
                || SatelliteRepos.Any(r => line.Contains($"Systemorph/{r}.git", StringComparison.OrdinalIgnoreCase))
                // …and anything that reads the checked-out tree
                || Regex.IsMatch(line, @"(^|[\s""'=/])plugins-repo([/\s""']|$)", Loose);

            if (actionable)
                hits.Add($"line {i + 1}: {line.Trim()}");
        }

        return hits.ToImmutable();
    }

    // ── the API-READ detector (#2689) ─────────────────────────────────────────────────────
    //
    // Two forms, and the first is the one that matters. A GitHub App installation token's
    // `repositories:` register IS the capability: it is the only place a satellite is named
    // LITERALLY, because the call sites that spend the token build their URLs from runtime
    // variables (`repos/$repo/contents/AGENTS.md`) and would otherwise be invisible. The second
    // form catches a call written out in full — `api.github.com/repos/Systemorph/…` or the `gh
    // api repos/Systemorph/…` shorthand — which reaches the repo whether or not a register
    // named it.
    //
    // A register whose value is an EXPRESSION (`${{ steps.x.outputs.name }}`) names no satellite
    // and is correctly silent: that is the node-repo-* lanes, which are CALLED BY a satellite and
    // scope the token to their caller — the arrow pointing the right way.

    private static readonly Regex TokenRegister =
        new(@"^\s*repositories:\s*(?<value>.*)$", Loose | RegexOptions.Compiled);

    private static ImmutableArray<string> CrossRepoApiHits(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var hits = ImmutableArray.CreateBuilder<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (CommentLine.IsMatch(line))
                continue;

            // A call written out in full. IgnoreCase for the same reason ActionableHits is:
            // GitHub resolves owner/repo case-insensitively, so a lowercased call is the same read.
            if (SatelliteRepos.Any(r => Regex.IsMatch(line, $@"repos/Systemorph/{Regex.Escape(r)}\b", Loose)))
            {
                hits.Add($"line {i + 1}: {line.Trim()}");
                continue;
            }

            var register = TokenRegister.Match(line);
            if (!register.Success)
                continue;

            // A folded scalar (`>-`, `>`, `|`) puts the value on the following line(s). Reading
            // only the key's own line would miss EVERY register in this repo, since all of them
            // are written folded — the guard would then ledger nothing and pass.
            var value = register.Groups["value"].Value.Trim();
            if (value.Length == 0 || value is ">-" or ">" or "|" or "|-" or ">+" or "|+")
            {
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].Trim().Length == 0 || CommentLine.IsMatch(lines[j]))
                        continue;
                    value = lines[j].Trim();
                    break;
                }
            }

            if (SatelliteRepos.Any(r => Regex.IsMatch(value, $@"(^|[,\s]){Regex.Escape(r)}($|[,\s])", Loose)))
                hits.Add($"line {i + 1}: {line.Trim()} -> {value}");
        }

        return hits.ToImmutable();
    }
}
