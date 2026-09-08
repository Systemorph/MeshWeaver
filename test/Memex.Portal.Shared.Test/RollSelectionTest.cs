#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The roll-selection algorithm (#3479), against the shared predicate and a real published
/// root on disk.</b>
///
/// <blockquote>Maintainer, 2026-09-06: <i>"Whenever a new platform / plugin is published, check for
/// each environment which is the latest platform version shipping all plugins, if different from
/// current version ⇒ update."</i></blockquote>
///
/// <para>Nothing is mocked: each candidate is judged by
/// <see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts)"/> over a <see cref="PublishedBundleCatalogue"/>
/// observation of a root this test writes — the same code path
/// <c>ReleaseAvailabilityService.IsUpdatable</c> runs in production. What is under test is the WALK
/// and its denominator discipline.</para>
///
/// <para><b>The fixture is a measured incident, not a synthetic one.</b> It is the shape of
/// <c>memex.meshweaver.cloud</c>'s own last availability verdict, read off
/// <c>Admin/UpdatePolicy</c> on 2026-09-06:</para>
/// <code>
/// heldTag    = 3.0.0-rc9.ci.7676
/// heldReason = "Analysis: no sealed content bake for framework identity
///               sf61a0f5d751c483216ea4a8d9883570f — the bundle 'Analysis' is not published for
///               release 3.0.0-rc9.ci.7676 …; …; Feedback: no sealed content bake for framework
///               identity sf61a0f5d751c483216ea4a8d9883570f — the bundle 'Feedback' is not
///               published for release 3.0.0-rc9.ci.7676 …"      (38 packages, of 77 installed)
/// </code>
/// <para>That environment has been sitting on <c>3.0.0-rc9.ci.7693</c> with <c>policy: None</c> ever
/// since. The gate was right and had nothing to offer instead — there was no way to ask "then which
/// release SHOULD I be on". That is the gap these tests pin.</para>
///
/// <para>🚨 <b>Since #3651 a missing bake is a COST, not a decline</b> (maintainer rule of
/// 2026-09-07): the newest release is selected and the outcome names what it recompiles at boot.
/// The measured fixture above therefore selects <c>ci.7676</c> by default, naming the eight —
/// and the walk that declines it survives under <c>Modules:RequirePrebuilt</c>, the opt-in strict
/// mode, which is the arm the laziness and "behind current" cases below run on because those
/// shapes need a decline to exist. The decline that exists everywhere now — an UNLOADABLE module —
/// is pinned in <see cref="ReleaseLinkGateTest"/>.</para>
/// </summary>
public class RollSelectionTest : IDisposable
{
    // ── the measured fixture ────────────────────────────────────────────────────────────────────

    /// <summary>The identity 3.0.0-rc9.ci.7676 resolved, quoted from the hold reason above.</summary>
    private const string IncompleteIdentity = "sf61a0f5d751c483216ea4a8d9883570f";
    private const string IncompleteVersion = "3.0.0-rc9.ci.7676";

    private const string CompleteIdentity = "s3479complete00000000000000000000";
    private const string CompleteVersion = "3.0.0-rc9.ci.7647";

    private const string RunningVersion = "3.0.0-rc9.ci.7693";

    /// <summary>
    /// A twelve-package slice of the environment's real install records (77 of them, measured
    /// 2026-09-06 via <c>search 'namespace:Plugins scope:children nodeType:Package'</c>). The first
    /// eight are packages the real hold NAMED as missing; the last four it did not — so the fixture
    /// reproduces a PARTIAL publication rather than an all-or-nothing one, which is the case a
    /// presence check has to get right.
    /// </summary>
    private static readonly string[] NamedInTheHold =
        ["Feedback", "Crm", "Edu", "Essentials", "Hosting", "Publish", "Store", "Training"];

    private static readonly string[] NotNamedInTheHold =
        ["AI", "Anthropic", "Mcp", "WebSearch"];

    private static string[] AllPackages => [.. NamedInTheHold, .. NotNamedInTheHold];

    private static ImmutableArray<RequiredPackage> Installed =>
        [.. AllPackages.Select(id => new RequiredPackage(id, id))];

    // ── the walk ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE ALGORITHM, as of #3651. The newest release does not publish a bundle for eight of
    /// the twelve installed packages; the one below it publishes all twelve. The selector takes
    /// the NEWEST — a missing bake is a boot compile, not a reason to walk on — and the outcome
    /// <b>names the eight</b>, Feedback among them, as the cost.
    /// </summary>
    [Fact]
    public async Task SelectsTheNewestRelease_NamingThePackagesItRecompilesAtBoot()
    {
        var root = MeasuredRoot();

        var outcome = await Select(root, RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.Update, outcome.Kind);
        Assert.Equal(IncompleteVersion, outcome.SelectedVersion);
        Assert.Empty(outcome.Declined);

        // The denominator is stated, not inferred from the pass — the whole #3441 lesson.
        Assert.Equal(AllPackages.Length, outcome.RequiredPlugins);
        Assert.Equal(AllPackages.Length, outcome.SatisfiedPlugins);
        Assert.Contains($"{AllPackages.Length} plugin(s) required", outcome.Summary);

        // …and so is the COST, on the outcome and in its summary.
        Assert.Equal(
            NamedInTheHold.Order(StringComparer.Ordinal),
            outcome.BootCompiles.Order(StringComparer.Ordinal));
        Assert.Contains("would recompile at boot", outcome.Summary);
        Assert.Contains("Feedback", outcome.Summary);
        Assert.Contains(outcome.Advisories, a => a.Contains(IncompleteIdentity, StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 THE ALGORITHM under the opt-in strict mode (<c>Modules:RequirePrebuilt</c>), which is
    /// what it was everywhere before #3651: the newest release is declined — <b>naming
    /// Feedback</b> — and the complete one is selected.
    /// </summary>
    [Fact]
    public async Task UnderRequirePrebuilt_SelectsTheLatestReleaseThatShipsEveryInstalledPlugin()
    {
        var root = MeasuredRoot();

        var outcome = await Select(root, RunningVersion, [IncompleteVersion, CompleteVersion], Strict)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.Update, outcome.Kind);
        Assert.Equal(CompleteVersion, outcome.SelectedVersion);
        Assert.Equal(AllPackages.Length, outcome.RequiredPlugins);
        Assert.Equal(AllPackages.Length, outcome.SatisfiedPlugins);
        Assert.Empty(outcome.BootCompiles);

        var declined = Assert.Single(outcome.Declined);
        Assert.Equal(IncompleteVersion, declined.Version);
        Assert.Contains("Feedback", declined.Reason);
        Assert.Contains(IncompleteIdentity, declined.Reason);
        Assert.Equal(
            NamedInTheHold.Order(StringComparer.Ordinal),
            declined.Blockers.Select(b => b.Package).Order(StringComparer.Ordinal));
        Assert.All(
            declined.Blockers,
            b => Assert.Equal(PackageAvailabilityKind.ContentBakeMissing, b.Kind));
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and the reason the assertion above could have failed. Run the SAME
    /// fixture with an empty denominator — which is what a selector that tolerates "no plugins to
    /// check" effectively has — and the incomplete release passes the shared predicate outright.
    /// A vacuity-tolerant walk therefore takes the newest, which is precisely the order #3479
    /// inverts.
    ///
    /// <para>So the selector refuses it by name instead: <see cref="RollSelectionKind.NoPluginsKnown"/>
    /// selects NOTHING and says why. "No plugins to check" and "all plugins present" are not spelled
    /// the same way here.</para>
    /// </summary>
    [Fact]
    public async Task AnEmptyDenominatorRefusesInsteadOfTakingTheNewest()
    {
        var root = MeasuredRoot();

        // What the predicate says about the incomplete release when nothing is required of it.
        var observation = PublishedBundleCatalogue.Read(root, IncompleteVersion);
        var vacuous = ReleaseAvailability.IsUpdatable(observation.Target, [], observation.Artifacts);
        Assert.True(
            vacuous.IsUpdatable,
            "with an empty denominator every release is vacuously complete — this is the state the "
            + "selector must never turn into a selection");

        var outcome = await RollSelection.Select(
                new RollSelectionInputs(
                    "memex",
                    RunningVersion,
                    [IncompleteVersion, CompleteVersion],
                    PluginInventory.Of([], "a reader that answered with nothing")),
                version => Verdict(root, [], version))
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.NoPluginsKnown, outcome.Kind);
        Assert.Null(outcome.SelectedVersion);
        Assert.True(outcome.IsIndeterminate);
        Assert.False(outcome.ShouldUpdate);
        Assert.Equal(0, outcome.RequiredPlugins);
        Assert.Contains("0 plugins required", outcome.Summary);
        Assert.Contains("vacuous", outcome.Summary);
    }

    /// <summary>
    /// A plugin list that could not be READ is a different answer again, and it holds. Collapsing it
    /// into "nothing to check" is the same defect one step earlier.
    /// </summary>
    [Fact]
    public async Task AnUnreadableInventoryHoldsAndIsNotAnEmptyOne()
    {
        var root = MeasuredRoot();

        var outcome = await RollSelection.Select(
                new RollSelectionInputs(
                    "memex",
                    RunningVersion,
                    [IncompleteVersion, CompleteVersion],
                    PluginInventory.Unreadable("the install records", "the query timed out")),
                version => Verdict(root, Installed, version))
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.Indeterminate, outcome.Kind);
        Assert.Null(outcome.SelectedVersion);
        Assert.True(outcome.IsIndeterminate);
        Assert.Contains("the query timed out", outcome.Summary);
        Assert.Contains("not clearance", outcome.Summary);
    }

    /// <summary>
    /// When NO release ships all plugins, a strict instance stays put and NAMES the plugin — an
    /// alert, never a silent hold and never "the closest one". (By default the same fixture
    /// selects the newest and names the eight as its boot compile — the first test above.)
    /// </summary>
    [Fact]
    public async Task UnderRequirePrebuilt_WhenNoReleaseShipsEveryPluginNothingIsSelectedAndThePluginIsNamed()
    {
        var root = Track(NewRoot());
        Mark(root, IncompleteVersion, IncompleteIdentity);
        Mark(root, CompleteVersion, CompleteIdentity);
        // Both publications miss the same eight packages.
        Seal(root, IncompleteIdentity, "plugins", NotNamedInTheHold);
        Seal(root, CompleteIdentity, "plugins", NotNamedInTheHold);

        var outcome = await Select(root, RunningVersion, [IncompleteVersion, CompleteVersion], Strict)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.NoCompleteRelease, outcome.Kind);
        Assert.Null(outcome.SelectedVersion);
        Assert.False(outcome.ShouldUpdate);
        // 🚨 NOT indeterminate: the selector looked, and the answer is about the publications.
        Assert.False(outcome.IsIndeterminate);
        Assert.Equal(2, outcome.Declined.Length);
        Assert.Contains("Feedback", outcome.Summary);
        Assert.Contains($"{AllPackages.Length} plugin(s) required", outcome.Summary);
        Assert.Contains(RunningVersion, outcome.Summary);
    }

    /// <summary>
    /// 🚨 The newest complete release sits BEHIND what the environment runs. That is reported and
    /// never applied: the environment is running something that does not ship all its plugins, which
    /// an operator must know, but rolling backwards is a separate decision (a schema migration is
    /// not reversible) and the selector does not take it.
    /// </summary>
    [Fact]
    public async Task TheNewestCompleteReleaseBehindTheRunningOneIsReportedNeverRolled()
    {
        var root = MeasuredRoot();
        // The running version publishes nothing at all — a marker with an identity that holds no
        // publication, exactly what PublishedBundleCatalogue answers ContentBakeMissing for. The
        // strict policy is what makes that a decline (by default it is a boot compile and the
        // running version is AlreadyCurrent).
        Mark(root, RunningVersion, "s3479running0000000000000000000");
        Directory.CreateDirectory(Path.Combine(root, "s3479running0000000000000000000"));

        var outcome = await Select(
                root, RunningVersion, [RunningVersion, IncompleteVersion, CompleteVersion], Strict)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.BehindCurrent, outcome.Kind);
        Assert.Equal(CompleteVersion, outcome.SelectedVersion);
        Assert.False(outcome.ShouldUpdate);
        Assert.Contains("BEHIND", outcome.Summary);
        Assert.Contains("never taken here", outcome.Summary);
    }

    /// <summary>
    /// The steady state is SAID, not left as a silence: the newest complete release is the one
    /// already running.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentAlreadyOnTheNewestCompleteReleaseIsToldSo()
    {
        var root = Track(NewRoot());
        Mark(root, CompleteVersion, CompleteIdentity);
        Seal(root, CompleteIdentity, "plugins", AllPackages);

        var outcome = await Select(root, CompleteVersion, [CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.AlreadyCurrent, outcome.Kind);
        Assert.Equal(CompleteVersion, outcome.SelectedVersion);
        Assert.False(outcome.ShouldUpdate);
        Assert.Contains("already runs", outcome.Summary);
    }

    /// <summary>
    /// 🚨 <b>The measured LIMIT of this algorithm, pinned so nobody has to rediscover it.</b>
    ///
    /// <para>#3479 proposed <c>3.0.0-ci.7926</c> as the acceptance case: memex.systemorph.com rolled
    /// to it at 19:17Z, its bake gate found <c>Feedback/Feedback</c> regressed, and every deal and
    /// offer page died (#3472/#3478). The stated expectation was that a completeness selector would
    /// have declined 7926 naming Feedback.</para>
    ///
    /// <para><b>It would not have, and the reason is measured.</b> CD run 7926
    /// (<c>actions/runs/34050278127</c>, all 26 jobs green) resolved framework identity
    /// <c>sc273ee39fdccbfc088f9aaf1fc548a9a</c> and its bake+seal job logged, on both storage
    /// targets:</para>
    /// <code>
    /// ok  Feedback/Feedback [5 source(s), 9 dependency record entr(ies)]
    /// bake: Feedback → 1 assembly(ies) + 15 node file(s) → /bake/Feedback.zip
    /// [PASS] Feedback (13 node(s), 1 type(s))    ok  Feedback/Feedback: compile=Ok render=ok tests=ok
    /// published: …/prebuilt-bundles/sc273ee39fdccbfc088f9aaf1fc548a9a/plugins/Feedback.zip
    /// sealed:    …/prebuilt-bundles/sc273ee39fdccbfc088f9aaf1fc548a9a/plugins/_complete (36 bundle(s))
    /// </code>
    /// <para><b>7926 shipped Feedback.</b> What failed was the INSTANCE: memex.systemorph.com's own
    /// copy of the package is missing one source node (<c>Feedback/Feedback/Source/FeedbackContent</c>,
    /// absent since 2026-08-26 — the node's compile record shows 4 matched Code nodes and
    /// <c>src=4</c>), which had been masked for eleven days by an <c>AdoptedUnverified</c> prebuilt
    /// assembly. The identity change made that assembly stale, the pre-warmer compiled the live —
    /// incomplete — source set, and 135 diagnostics later the readiness gate refused.</para>
    ///
    /// <para>So the selector's reach stops at the PUBLICATION, and this test says so out loud: a
    /// release that ships every plugin is selected even when this instance will fail to build one of
    /// them. The checks that DO see that are instance-side — the combo gate
    /// (<c>Doc/Architecture/ComboGateWiring</c>) and the bake sweep itself — and #3478's rule that a
    /// refused process must not join the mesh. Weakening the selector to cover it would be
    /// guesswork; recording the boundary is not.</para>
    /// </summary>
    [Fact]
    public async Task AReleaseThatShipsEveryPluginIsSelectedEvenWhenTheInstanceCannotBuildOne()
    {
        const string Ci7926 = "3.0.0-ci.7926";
        const string Identity7926 = "sc273ee39fdccbfc088f9aaf1fc548a9a";

        var root = Track(NewRoot());
        Mark(root, Ci7926, Identity7926);
        Seal(root, Identity7926, "plugins", AllPackages);

        var outcome = await Select(root, RunningVersion, [Ci7926])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.Update, outcome.Kind);
        Assert.Equal(Ci7926, outcome.SelectedVersion);
        Assert.Empty(outcome.Declined);
        Assert.Equal(AllPackages.Length, outcome.SatisfiedPlugins);
    }

    /// <summary>
    /// The walk is LAZY: the candidate below the first acceptance is never asked about. That is what
    /// keeps a selection over a long publication history one directory read rather than hundreds on
    /// a network share against a bounded budget.
    /// </summary>
    [Fact]
    public async Task TheWalkStopsAtTheFirstCompleteRelease()
    {
        var root = MeasuredRoot();
        var asked = ImmutableList.CreateBuilder<string>();

        var outcome = await RollSelection.Select(
                new RollSelectionInputs(
                    "memex",
                    RunningVersion,
                    [IncompleteVersion, CompleteVersion, "3.0.0-rc9.ci.7600"],
                    PluginInventory.Of(Installed, "the install records")),
                // No guard around the builder: the walk is sequential by construction — the next
                // candidate's verdict is only asked for inside the previous one's decline — and
                // that is precisely what this test measures. Strict, so that a decline exists.
                version =>
                {
                    asked.Add(version);
                    return Verdict(root, Installed, version, Strict);
                })
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(CompleteVersion, outcome.SelectedVersion);
        Assert.Equal([IncompleteVersion, CompleteVersion], asked.ToImmutable());
    }

    // ── driving the SHARED predicate ────────────────────────────────────────────────────────────

    /// <summary>The opt-in strict mode — <c>Modules:RequirePrebuilt</c> — under which a missing
    /// bake is still a decline (#3651).</summary>
    private static readonly ReleaseGatePolicy Strict = new(RequirePrebuilt: true);

    /// <summary>The selection, judged by exactly the predicate the deployment gate runs.</summary>
    private static IObservable<RollSelectionOutcome> Select(
        string root, string? current, ImmutableArray<string> candidatesNewestFirst,
        ReleaseGatePolicy? policy = null) =>
        RollSelection.Select(
            new RollSelectionInputs(
                "memex",
                current,
                candidatesNewestFirst,
                PluginInventory.Of(Installed, "the install records (Plugins/*, nodeType:Package)")),
            version => Verdict(root, Installed, version, policy));

    /// <summary>🚨 The SHARED predicate, not a copy of it: the same
    /// <see cref="PublishedBundleCatalogue.Read"/> + <see cref="ModuleLinkObservation.Measure"/> +
    /// <see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts, ReleaseGatePolicy)"/>
    /// composition <c>ReleaseAvailabilityService</c> runs in production.</summary>
    private static IObservable<UpdatabilityVerdict> Verdict(
        string root, ImmutableArray<RequiredPackage> required, string version,
        ReleaseGatePolicy? policy = null) =>
        Observable.Defer(() =>
        {
            var observation = PublishedBundleCatalogue.Read(root, version);
            var measured = ModuleLinkObservation.Measure(observation.Artifacts, required);
            return Observable.Return(ReleaseAvailability.IsUpdatable(
                observation.Target, required, measured, policy ?? ReleaseGatePolicy.Default));
        });

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The measured shape: an older identity that sealed every package, and the newer one
    /// that sealed only the four the real hold did not name.</summary>
    private string MeasuredRoot()
    {
        var root = Track(NewRoot());
        Mark(root, IncompleteVersion, IncompleteIdentity);
        Mark(root, CompleteVersion, CompleteIdentity);
        Seal(root, CompleteIdentity, "plugins", AllPackages);
        Seal(root, IncompleteIdentity, "plugins", NotNamedInTheHold);
        return root;
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "mw-3479-" + Guid.NewGuid().ToString("N"));

    /// <summary>The release marker: version → the framework identity that release resolves. The one
    /// way anything outside the image can learn it.</summary>
    private static void Mark(string root, string version, string identity)
    {
        var markers = Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, version), identity);
    }

    /// <summary>A SEALED source publication: the bundles, then the sentinel that lists them.</summary>
    private static void Seal(string root, string identity, string source, IEnumerable<string> bundles)
    {
        var directory = Path.Combine(root, identity, source);
        Directory.CreateDirectory(directory);
        var names = new List<string>();
        foreach (var bundle in bundles)
        {
            WriteBundle(Path.Combine(directory, bundle + ".zip"), bundle, identity);
            names.Add(bundle + ".zip");
        }
        File.WriteAllText(
            Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            string.Join('\n', names.Order(StringComparer.Ordinal)) + "\n");
    }

    /// <summary>A bundle carrying a real manifest with no module dependency, so the #3175 sealed-set
    /// consistency rule has nothing to say and these tests measure only completeness.</summary>
    private static void WriteBundle(string path, string bundle, string identity)
    {
        var manifest = new BundleReader.Manifest(
            bundle, "1.0", identity,
            [
                new BundleReader.AssemblyRef(
                    $"{bundle}/Type", $"{bundle}_Type.dll",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MeshWeaver.Layout"] = MeshWeaver.Compiler.CompiledDependencies.RefAsmScheme + "abc",
                    }),
            ]);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(NuGetPackageWriter.ManifestEntry).Open();
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(
            manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    /// <summary>Every published root this test built, removed on teardown — a leaked temp tree
    /// bloats the CI agent and is one more way for two runs to interfere.</summary>
    private readonly List<string> roots = [];

    private string Track(string root)
    {
        roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }
}
