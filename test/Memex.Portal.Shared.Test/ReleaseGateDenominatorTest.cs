#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Hosting;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The deployment gate's DENOMINATOR may not be read from the artifact under judgement
/// (#3441).</b>
///
/// <para>Maintainer, 2026-09-06: <i>"let's see that the deploy rolls only when all packages of all
/// plugins are baked"</i> — <i>"we kept rolling without edu being properly baked"</i>.</para>
///
/// <para>The gate asked which installed packages are content-bearing by reading the sealed bundle
/// set of the identity the instance is running NOW. That is the same artifact store the gate is
/// about to judge, so the denominator was a function of the very thing that breaks: the moment
/// Education's bake stopped being produced, its packages stopped being asked about, and every
/// later roll was green about precisely the packages that had regressed. In the limit — an
/// identity with no publication at all — every installed package became non-content-bearing and
/// the content half of the gate checked NOTHING while reporting updatable. "0 expected, 0 found,
/// green" is the shape a completeness gate exists to make impossible.</para>
///
/// <para>These tests are the falsification, both ways, on the reconstructed Education state. The
/// OLD reading is computed alongside the new one in <see cref="TheOldDenominatorPassed_TheNewOneHolds"/>
/// so the negative control is executed rather than asserted: a test that only showed the new
/// reading holding could not distinguish "the fix works" from "the fixture was always red".</para>
/// </summary>
public class ReleaseGateDenominatorTest : IDisposable
{
    /// <summary>Every published root this test built, removed on teardown — a leaked temp tree
    /// bloats the CI agent and is one more way for two runs to interfere.</summary>
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private string Track(string root)
    {
        roots.Add(root);
        return root;
    }

    // Three framework identities: an EARLIER wave that baked everything, the identity the instance
    // is RUNNING (Education's publication already torn), and the TARGET the roll would move to.
    private const string Earlier = "s3441earlier00000000000000000000";
    private const string Live = "s3441live00000000000000000000000";
    private const string Target = "s3441target00000000000000000000";
    private const string TargetVersion = "3.0.0-rc9.ci.3441";

    /// <summary>Education's nine courses — the repo's declared package set, measured 2026-09-06
    /// from the top-level directories carrying an <c>index.json</c>.</summary>
    private static readonly string[] EducationPackages =
    [
        "AdvancedBusinessRules", "AgenticBusiness", "AgenticEngineering", "AgenticOffice",
        "AgenticPrimer", "AgenticPrimerDe", "DataImportExport", "DataModeling", "ThinkInStreams",
    ];

    private static readonly string[] PlatformPackages = ["Documentation", "Northwind"];
    private static readonly string[] PluginPackages = ["Store", "AI"];

    /// <summary>A package that ships a compiled module and NO NodeType content: it produces no
    /// bundle under ANY identity, ever. The exemption that made the old reading attractive, and
    /// which the new reading must preserve — demanding a bundle here would freeze the environment
    /// forever.</summary>
    private const string ModuleOnlyPackage = "Hosting.Instance";

    // ── the denominator itself ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EverSealed_ReadsEveryIdentity_NotOnlyTheLiveOne()
    {
        var root = EducationRegressedRoot();

        var floor = PublishedBundleCatalogue.EverSealedBundles(root);

        // Non-zero, and printed here for the same reason the service logs it: the claim IS the
        // count. Three identities published; the earlier one is where Education's courses live.
        Assert.Equal(3, floor.Identities);
        Assert.True(floor.ServesBakes);
        foreach (var package in EducationPackages)
            Assert.Contains(package, floor.Bundles);
        Assert.Equal(
            EducationPackages.Length + PlatformPackages.Length + PluginPackages.Length,
            floor.Bundles.Count);

        // …and the module-only package is still absent, under every identity. The exemption is
        // preserved: it is not that we now demand everything, it is that we no longer forget.
        Assert.DoesNotContain(ModuleOnlyPackage, floor.Bundles);
    }

    [Fact]
    public void ARootThatServesNoBakes_IsNotEnforced_RatherThanASilentPass()
    {
        // A configured root holding no sealed publication under any identity. The old reading made
        // this the WORST case — every package non-content-bearing, the content half checking
        // nothing, and a green verdict over zero evidence.
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-3441-empty-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        // A torn publication contributes nothing — it is not evidence that the root serves bakes.
        Directory.CreateDirectory(Path.Combine(root, Live, "education"));

        var floor = PublishedBundleCatalogue.EverSealedBundles(root);

        Assert.Equal(0, floor.Identities);
        Assert.False(floor.ServesBakes);
        Assert.Empty(floor.Bundles);
    }

    /// <summary>
    /// 🚨 <b>An ABSENT root is a refusal, not an exemption</b> — the two produce the same empty
    /// floor and mean opposite things.
    ///
    /// <para>A deployment that configures a bundle root DECLARES that it consumes CI bakes. If that
    /// root is not on disk, the volume did not mount or the path is mistyped: an availability
    /// incident. Reading it as "serves no bakes" would clear the gate on a mis-mount — the very
    /// vacuity this change removes, reintroduced one level up. Caught in review on this PR.</para>
    /// </summary>
    [Fact]
    public void AnAbsentRoot_Refuses_RatherThanReadingAsServesNoBakes()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mw-3441-absent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        var floor = PublishedBundleCatalogue.EverSealedBundles(missing);

        Assert.NotNull(floor.Refusal);
        Assert.Contains(missing, floor.Refusal);
        // 🚨 ServesBakes is false here TOO — which is exactly why a caller must test Refusal first.
        Assert.False(floor.ServesBakes);

        // And the refusal becomes the HOLD the service actually produces — never an exemption.
        // 🚨 This is the SAME construction ReleaseAvailabilityService.Verdict uses, deliberately:
        // IsUpdatable(target, [], Unreadable(...)) would answer IsUpdatable=false but
        // IsIndeterminate=FALSE (that property reads the per-package verdicts, and an empty list
        // has none), which surfaces an availability incident as a compatibility verdict.
        var verdict = UpdatabilityVerdict.Unavailable(floor.Refusal!);
        Assert.False(verdict.IsUpdatable);
        Assert.True(verdict.IsIndeterminate);
        Assert.Equal(floor.Refusal, verdict.HoldReason);
        Assert.All(verdict.Packages, p =>
            Assert.Equal(PackageAvailabilityKind.Indeterminate, p.Kind));
    }

    /// <summary>
    /// 🚨 The denominator reads the sentinel's DECLARATION, so a publication that is sealed but has
    /// LOST a bundle still counts that package as content-bearing.
    ///
    /// <para>Inclusive on purpose, and it is the safe direction: keeping the package in the set the
    /// gate asks about can only HOLD a roll, never exempt one. Verifying presence here would also
    /// cost one stat per bundle per source per identity on a network share, against a 60 s verdict
    /// budget — and a denominator that times out answers Indeterminate, which freezes every
    /// environment. The presence check stays where it decides the verdict: the target identity.</para>
    /// </summary>
    [Fact]
    public void ASealedPublicationMissingABundle_StillCountsTowardTheDenominator()
    {
        var root = EducationRegressedRoot();
        // The earlier identity's Education publication loses a bundle's bytes but keeps its seal.
        File.Delete(Path.Combine(root, Earlier, "education", EducationPackages[0] + ".zip"));

        var floor = PublishedBundleCatalogue.EverSealedBundles(root);
        Assert.Contains(EducationPackages[0], floor.Bundles);

        // The full presence rule is unchanged where it decides the verdict — that identity's
        // Education source is torn and contributes NOTHING to what can be adopted.
        var adoptable = PublishedBundleCatalogue.SealedBundlesForIdentity(root, Earlier);
        Assert.DoesNotContain(EducationPackages[0], adoptable);
        Assert.DoesNotContain(EducationPackages[1], adoptable);
    }

    // ── the verdict, both ways, on the reconstructed Education state ─────────────────────────────

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION. One fixture, two denominators, opposite verdicts — so the
    /// negative control is a measurement rather than a claim.
    /// </summary>
    [Fact]
    public void TheOldDenominatorPassed_TheNewOneHolds()
    {
        var root = EducationRegressedRoot();
        var observation = PublishedBundleCatalogue.Read(root, TargetVersion);
        Assert.Equal(Target, observation.Target.FrameworkIdentity);

        // ── the OLD reading: content-bearing = sealed under the identity running NOW ──
        var old = PublishedBundleCatalogue.SealedBundlesForIdentity(root, Live);
        foreach (var course in EducationPackages)
            Assert.DoesNotContain(course, old);          // already fallen out of the denominator
        var oldVerdict = ReleaseAvailability.IsUpdatable(
            observation.Target, Required(old), observation.Artifacts);

        // The control: with the old denominator this roll PROCEEDS, over an Education publication
        // that is not there. This is what "we kept rolling without edu being properly baked" was.
        Assert.True(oldVerdict.IsUpdatable);
        Assert.Empty(oldVerdict.Blockers);

        // ── the NEW reading: content-bearing = sealed under ANY identity this root holds ──
        var floor = PublishedBundleCatalogue.EverSealedBundles(root);
        var newVerdict = ReleaseAvailability.IsUpdatable(
            observation.Target, Required(floor.Bundles), observation.Artifacts);

        Assert.False(newVerdict.IsUpdatable);

        // It names exactly which packages, and says what is wrong with them.
        var blockers = newVerdict.Blockers.ToList();
        Assert.Equal(EducationPackages.Length, blockers.Count);
        Assert.Equal(
            EducationPackages.OrderBy(p => p, StringComparer.Ordinal),
            blockers.Select(b => b.Package).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(blockers, b =>
            Assert.Equal(PackageAvailabilityKind.ContentBakeMissing, b.Kind));
        Assert.Contains(Target, newVerdict.HoldReason);

        // The module-only package is NOT among them — the preserved exemption, asserted on the
        // same run that produces the hold, so "it holds" cannot be hiding "it holds everything".
        Assert.DoesNotContain(ModuleOnlyPackage, blockers.Select(b => b.Package));
    }

    [Fact]
    public void ACompleteEducationBake_Greens()
    {
        var root = EducationRegressedRoot();
        // The operator does what the refusal asks: re-run Education's bake for the target identity.
        Seal(root, Target, "education", EducationPackages);

        var observation = PublishedBundleCatalogue.Read(root, TargetVersion);
        var floor = PublishedBundleCatalogue.EverSealedBundles(root);
        var verdict = ReleaseAvailability.IsUpdatable(
            observation.Target, Required(floor.Bundles), observation.Artifacts);

        Assert.True(verdict.IsUpdatable);
        Assert.Empty(verdict.Blockers);
        Assert.Null(verdict.HoldReason);

        // 🚨 The denominator was non-zero on the green arm too. A pass over an empty expected set
        // is exactly what this gate exists to prevent, so the green arm asserts WHAT WAS DEMANDED
        // — 13 content-bearing packages of 14 installed — not merely that nothing blocked.
        var demanded = Required(floor.Bundles).Count(p => p.HasContent);
        Assert.Equal(
            EducationPackages.Length + PlatformPackages.Length + PluginPackages.Length, demanded);
        Assert.Equal(demanded + 1, verdict.Packages.Length);   // + the exempt module-only package
        Assert.All(verdict.Packages, p =>
            Assert.Equal(PackageAvailabilityKind.Available, p.Kind));
    }

    /// <summary>
    /// A package that regressed to a PARTIAL bake — some courses sealed, some not — is named
    /// package by package. The maintainer's ask is "all packages of all plugins", so a source that
    /// seals four of nine must not read as a sealed source.
    /// </summary>
    [Fact]
    public void APartialEducationBake_NamesTheMissingCoursesOnly()
    {
        var root = EducationRegressedRoot();
        var shipped = EducationPackages.Take(4).ToArray();
        Seal(root, Target, "education", shipped);

        var observation = PublishedBundleCatalogue.Read(root, TargetVersion);
        var floor = PublishedBundleCatalogue.EverSealedBundles(root);
        var verdict = ReleaseAvailability.IsUpdatable(
            observation.Target, Required(floor.Bundles), observation.Artifacts);

        Assert.False(verdict.IsUpdatable);
        Assert.Equal(
            EducationPackages.Skip(4).OrderBy(p => p, StringComparer.Ordinal),
            verdict.Blockers.Select(b => b.Package).OrderBy(p => p, StringComparer.Ordinal));
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the gate's inputs are, given a denominator: every installed package, plus the
    /// module-only one that has never had a bundle.</summary>
    private static IEnumerable<RequiredPackage> Required(IReadOnlySet<string> contentBearing) =>
        EducationPackages
            .Concat(PlatformPackages)
            .Concat(PluginPackages)
            .Append(ModuleOnlyPackage)
            .Select(id => new RequiredPackage(id, id, null, contentBearing.Contains(id)));

    /// <summary>
    /// The measured shape of the incident: an EARLIER identity where every source baked, the LIVE
    /// identity and the TARGET identity where Education's publication is torn (present as a
    /// directory, no <c>_complete</c> sentinel — a bake that died before sealing, which is exactly
    /// what the boot seeder and the gate both refuse to read).
    /// </summary>
    private string EducationRegressedRoot()
    {
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-3441-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        File.WriteAllText(
            Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName, TargetVersion),
            Target);

        foreach (var identity in new[] { Earlier, Live, Target })
        {
            Seal(root, identity, "meshweaver-content", PlatformPackages);
            Seal(root, identity, "plugins", PluginPackages);
        }
        // Education baked once, under the earlier identity, and has not sealed since.
        Seal(root, Earlier, "education", EducationPackages);
        Tear(root, Live, "education");
        Tear(root, Target, "education");
        return root;
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

    /// <summary>A TORN publication: the directory exists, the sentinel does not.</summary>
    private static void Tear(string root, string identity, string source) =>
        Directory.CreateDirectory(Path.Combine(root, identity, source));

    /// <summary>A bundle carrying a real manifest with no module dependency, so the #3175
    /// sealed-set consistency rule has nothing to say and this test measures only completeness.</summary>
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
}
