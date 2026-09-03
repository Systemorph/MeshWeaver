#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Hosting;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>Availability is a CONSISTENT sealed set, not the presence of a file (#3175).</b>
///
/// <para>memex-cloud rolled to ci.7621 on a verdict that checked presence — every installed package
/// had a sealed bundle under the target's identity — and the portal then DECLINED SocialMedia at
/// adoption: its NodeTypes recorded <c>MeshWeaver.Markdown.Collaboration</c> at one MVID while the
/// module sealed for the same identity carried another. The same morning every satellite gate
/// declined four map galleries whose records named <c>MeshWeaver.Maps</c> at an MVID nothing
/// composed. Both are one defect seen twice: two producers of one module for one identity, and a
/// gate that could not see it because it never read the bytes that carry the answer.</para>
///
/// <para>These pin <see cref="PublishedBundleCatalogue.ArtifactsForIdentity"/> — the observation
/// now reads each sealed module bundle's entry assembly for its MVID and each sealed bundle's
/// dependency records — and <see cref="ReleaseAvailability.IsUpdatable"/>'s rule over them. Both
/// failure directions are pinned on purpose: a false "available" rolls a fleet onto a half-broken
/// platform; a false "hold" freezes every install (#1754), so an UNREADABLE module set is
/// <see cref="PackageAvailabilityKind.Indeterminate"/> and a module the set does not carry is not
/// judged at all.</para>
/// </summary>
public class SealedSetConsistencyTest
{
    private const string Identity = "s3175consistency0000000000000000";
    private const string Version = "3.0.0-rc9.ci.3175";
    private const string Module = "MeshWeaver.ProbeModule";
    private const string Bundle = "Widget";

    /// <summary>The module bundle carries a REAL managed assembly under the module folder — this
    /// test assembly's dependency, chosen because its MVID is knowable from the running process —
    /// so the observation's PE read is exercised against genuine bytes, not a stub.</summary>
    private static readonly System.Reflection.Assembly ModuleBytesOf = typeof(ReleaseAvailability).Assembly;

    private static string SealedMvid => MvidOf(ModuleBytesOf);

    private static string AnotherBuild =>
        CompiledDependencies.MvidScheme + Guid.NewGuid().ToString("N");

    /// <summary>The module-owned sibling of the #3221 fixtures — a REAL managed assembly, distinct
    /// from the entry's, so the observation's PE read answers a genuine MVID for the riding copy.
    /// Its simple name comes from the FILE name the bundle carries it under, which is exactly how the
    /// loader and the observation both name it.</summary>
    private const string Sibling = "MeshWeaver.ProbeSibling";

    private static byte[] SiblingBytes => File.ReadAllBytes(typeof(BundleReader).Assembly.Location);

    private static string SiblingMvid => MvidOf(typeof(BundleReader).Assembly);

    /// <summary>A DIFFERENT real build to stand in for "the same assembly, compiled in another
    /// wave" — the divergence the negative control needs.</summary>
    private static byte[] AnotherBuildBytes =>
        File.ReadAllBytes(typeof(CompiledDependencies).Assembly.Location);

    private static string AnotherBuildMvid => MvidOf(typeof(CompiledDependencies).Assembly);

    private static string MvidOf(System.Reflection.Assembly assembly) =>
        CompiledDependencies.MvidScheme + assembly.ManifestModule.ModuleVersionId.ToString("N");

    // ── the observation: the bytes on disk become a module set and dependency records ───────────

    [Fact]
    public void ASealedPublication_IsObservedWithItsModuleSetAndItsRecords()
    {
        var root = PublishedRoot(recorded: SealedMvid);
        try
        {
            var observation = PublishedBundleCatalogue.Read(root, Version);

            Assert.Equal(Identity, observation.Target.FrameworkIdentity);
            Assert.Null(observation.Artifacts.ReadFailure);
            Assert.Contains(Bundle, observation.Artifacts.SealedBundles);

            var modules = observation.Artifacts.Modules;
            Assert.NotNull(modules);
            Assert.Null(modules.Refusal);
            Assert.Empty(modules.Conflicts);
            Assert.Equal(SealedMvid, modules.MvidByModule[Module]);

            var record = Assert.Single(observation.Artifacts.DependencyRecords);
            Assert.Equal(Bundle, record.Bundle);
            Assert.Equal("Widget/Thing", record.NodePath);
            Assert.Equal(SealedMvid, record.Dependencies[Module]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ABundleBuiltAgainstTheSealedModule_IsAvailable()
    {
        var root = PublishedRoot(recorded: SealedMvid);
        try
        {
            var verdict = Verdict(root);
            Assert.True(verdict.IsUpdatable, verdict.HoldReason);
            Assert.Equal(PackageAvailabilityKind.Available, Assert.Single(verdict.Packages).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>THE incident: sealed, present, and built against a module build the same identity's
    /// sealed set does not carry. Presence said "available"; the portal declined it at adoption.</summary>
    [Fact]
    public void ABundleBuiltAgainstAnotherBuildOfTheSealedModule_IsHeld_NamingBothBuilds()
    {
        var recorded = AnotherBuild;
        var root = PublishedRoot(recorded);
        try
        {
            var verdict = Verdict(root);
            Assert.False(verdict.IsUpdatable);
            var package = Assert.Single(verdict.Packages);
            Assert.Equal(PackageAvailabilityKind.SealedSetInconsistent, package.Kind);
            Assert.False(verdict.IsIndeterminate,
                "two builds of one module is a DEFINITE inconsistency of the set, not an unreadability");
            Assert.NotNull(package.Reason);
            Assert.Contains(Bundle, package.Reason, StringComparison.Ordinal);
            Assert.Contains(Module, package.Reason, StringComparison.Ordinal);
            Assert.Contains(recorded, package.Reason, StringComparison.Ordinal);
            Assert.Contains(SealedMvid, package.Reason, StringComparison.Ordinal);
            Assert.Contains("dependency record mismatch", package.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 THE case that could falsify the fix: one sealed bundle that is NOT a readable archive — a
    /// file of literal bytes, deliberately not a zip — beside a readable, INCONSISTENT one. The read
    /// must still resolve the identity, count the unreadable bundle for presence (nothing for the
    /// consistency check to judge, as for a legacy bundle), and still JUDGE the rest of the set:
    /// the readable bundle built against another module build holds, by name. The first cut let the
    /// unreadable file throw out of <c>Read</c>'s outer catch, turning the WHOLE identity unreadable
    /// and losing its resolved identity — MeshWeaver.Plugins' catalogue tests (which stage bundles as
    /// literal bytes) went red on main within the hour of #3187 merging, and every portal reading
    /// such a root would have held every update with a reason nobody could act on.
    /// </summary>
    [Fact]
    public void AnUnreadableBundle_CountsForPresence_AndTheRestOfTheSetIsStillJudged()
    {
        var recorded = AnotherBuild;
        var root = PublishedRoot(recorded);
        try
        {
            // A second source whose only bundle is NOT an archive, sealed with an EMPTY module set
            // ("composed nothing" — a legitimate seal, distinct from "predates module sealing").
            var education = Path.Combine(root, Identity, "education");
            Directory.CreateDirectory(Path.Combine(education, PublishedBundleCatalogue.ModulesDirectoryName));
            File.WriteAllText(Path.Combine(education, "Doc.zip"), "bytes");
            File.WriteAllText(
                Path.Combine(education, PublishedBundleCatalogue.ModulesDirectoryName,
                    PublishedBundleCatalogue.ModulesIndexFileName), string.Empty);
            File.WriteAllText(
                Path.Combine(education, ShippedPrebuiltBundles.CompletionSentinelFileName), "Doc.zip\n");

            var observation = PublishedBundleCatalogue.Read(root, Version);

            Assert.Equal(Identity, observation.Target.FrameworkIdentity);
            Assert.Null(observation.Artifacts.ReadFailure);
            Assert.Contains("Doc", observation.Artifacts.SealedBundles);
            Assert.Contains(Bundle, observation.Artifacts.SealedBundles);
            // The unreadable bundle contributed no records; the readable one did.
            Assert.Equal([Bundle], observation.Artifacts.DependencyRecords.Select(r => r.Bundle).Distinct());
            Assert.NotNull(observation.Artifacts.Modules);
            Assert.Null(observation.Artifacts.Modules.Refusal);

            var verdict = ReleaseAvailability.IsUpdatable(
                observation.Target,
                [new RequiredPackage("Doc", "Doc"), Required],
                observation.Artifacts);

            Assert.False(verdict.IsUpdatable);
            Assert.False(verdict.IsIndeterminate, "an unreadable BUNDLE is not an unreadable SET");
            var doc = Assert.Single(verdict.Packages, p => p.Package == "Doc");
            Assert.Equal(PackageAvailabilityKind.Available, doc.Kind);
            var widget = Assert.Single(verdict.Packages, p => p.Package == Bundle);
            Assert.Equal(PackageAvailabilityKind.SealedSetInconsistent, widget.Kind);
            Assert.Contains(recorded, widget.Reason!, StringComparison.Ordinal);
            Assert.Contains(SealedMvid, widget.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── #3221: a module-owned sibling RIDES a second bundle, and every copy must be one build ────

    /// <summary>
    /// 🚨 THE SANCTIONED SHAPE, and the reason the closure rule was NOT changed (#3221). A
    /// module-owned <c>MeshWeaver.*</c> sibling rides every bundle that references it, so one
    /// assembly name is sealed by several bundles at once — measured on MeshWeaver.Plugins
    /// <c>main</c> 2026-09-03, 19 of 37 module bundles carry a copy of an assembly another package
    /// declares as its module. This must NOT hold: refusing it would freeze more than half the
    /// fleet's modules, and excluding declared modules from the closure would invert the package
    /// graph (<c>AI</c> requires only <c>Store</c> yet would need <c>Essentials</c>' module, while
    /// <c>Essentials</c> requires <c>AI</c>).
    /// </summary>
    [Fact]
    public void ASiblingRidingASecondBundle_AtTheSameBuild_IsAvailable()
    {
        var root = PublishedRootWithRide(
            recorded: SiblingMvid, declared: SiblingBytes, riding: SiblingBytes);
        try
        {
            var observation = PublishedBundleCatalogue.Read(root, Version);
            Assert.Empty(observation.Artifacts.Modules!.Conflicts);
            Assert.Null(observation.Artifacts.Modules.Refusal);
            // The DECLARED module defines the set; the ride agrees with it.
            Assert.Equal(SiblingMvid, observation.Artifacts.Modules.MvidByModule[Sibling]);

            var verdict = ReleaseAvailability.IsUpdatable(
                observation.Target, [Required], observation.Artifacts);
            Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 <b>THE NEGATIVE CONTROL for #3221.</b> The same set, with the RIDING copy a different build
    /// from the one its owning package declares. Every declared entry is still distinct and
    /// self-consistent — <c>MeshWeaver.ProbeModule</c> is sealed once, <c>MeshWeaver.ProbeSibling</c>
    /// is sealed once as a declared module — so the entry-only reading #3187 shipped sees a perfectly
    /// consistent set and answers AVAILABLE. Only a reading that accounts for the RIDING copy can
    /// fail here, which is what makes this a proof that the check binds to the sibling path and not
    /// merely to the path already covered.
    ///
    /// <para>The failure it prevents is the one memex-cloud suffered on ci.7621: the loader binds
    /// <c>MeshWeaver.*</c> by a strictly synchronised <c>AssemblyVersion</c>, so two copies under one
    /// simple name collapse to whichever loaded first and every NodeType that recorded the other is
    /// declined at adoption — <i>"dependency record mismatch — built against mvid:A, live is
    /// mvid:B"</i>.</para>
    /// </summary>
    [Fact]
    public void ASiblingRidingASecondBundle_AtAnotherBuild_IsHeld_NamingBothProducers()
    {
        var root = PublishedRootWithRide(
            recorded: SiblingMvid, declared: SiblingBytes, riding: AnotherBuildBytes);
        try
        {
            var observation = PublishedBundleCatalogue.Read(root, Version);

            // The set itself says so, naming both producers and the ROLE of each.
            var conflict = Assert.Single(observation.Artifacts.Modules!.Conflicts);
            Assert.StartsWith($"module {Sibling}:", conflict, StringComparison.Ordinal);
            Assert.Contains(AnotherBuildMvid, conflict, StringComparison.Ordinal);
            Assert.Contains(SiblingMvid, conflict, StringComparison.Ordinal);
            Assert.Contains("as a sibling riding 'probe.module.nupkg'", conflict, StringComparison.Ordinal);
            Assert.Contains("as the declared module of 'probe.sibling.nupkg'", conflict, StringComparison.Ordinal);
            // A name carried at two builds defines nothing — whichever loads first is a coin toss.
            Assert.DoesNotContain(Sibling, observation.Artifacts.Modules.MvidByModule.Keys);
            Assert.Null(observation.Artifacts.Modules.Refusal);

            var verdict = ReleaseAvailability.IsUpdatable(
                observation.Target, [Required], observation.Artifacts);

            Assert.False(verdict.IsUpdatable);
            Assert.False(verdict.IsIndeterminate,
                "two builds of one assembly name is a DEFINITE inconsistency of the set, not an unreadability");
            var package = Assert.Single(verdict.Packages);
            Assert.Equal(PackageAvailabilityKind.SealedSetInconsistent, package.Kind);
            Assert.Contains(conflict, package.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 The reading a ride must NOT change. Only the DECLARED module defines
    /// <see cref="SealedModuleSet.MvidByModule"/>: an instance registers declared modules as
    /// <c>InstalledModuleAssembly</c>, and that registration is what
    /// <c>NodeTypeCompilationHelpers.ModuleMvidsOf</c> reports back as "live". An assembly that only
    /// ever RIDES is registered nowhere, so letting it define the set would start judging records
    /// against bytes the instance never reports — a false HOLD, the expensive failure direction
    /// (#1754). Here <c>MeshWeaver.ProbeSibling</c> rides but nothing declares it, and a record naming
    /// it is left unjudged exactly as a module outside the sealed set is.
    /// </summary>
    [Fact]
    public void ARideOnlyAssembly_DoesNotDefineTheModuleSet()
    {
        var root = PublishedRootWithRide(
            recorded: AnotherBuildMvid, declared: null, riding: SiblingBytes);
        try
        {
            var observation = PublishedBundleCatalogue.Read(root, Version);
            Assert.Empty(observation.Artifacts.Modules!.Conflicts);
            Assert.Null(observation.Artifacts.Modules.Refusal);
            Assert.DoesNotContain(Sibling, observation.Artifacts.Modules.MvidByModule.Keys);
            Assert.Contains(Module, observation.Artifacts.Modules.MvidByModule.Keys);

            var verdict = ReleaseAvailability.IsUpdatable(
                observation.Target, [Required], observation.Artifacts);
            Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A non-<c>MeshWeaver.*</c> package riding two bundles at two builds is NOT a conflict:
    /// a third-party diamond rides by design (Module Closure Accounting) and versions independently,
    /// so it does not collapse to one identity the way a strictly-version-synchronised
    /// <c>MeshWeaver.*</c> assembly does. Judging it would hold every bundle in the fleet.</summary>
    [Fact]
    public void AThirdPartyDiamondRidingTwoBundles_IsNotJudged()
    {
        var root = PublishedRootWithRide(
            recorded: SiblingMvid, declared: SiblingBytes, riding: SiblingBytes,
            thirdParty: ("Contoso.Sdk", AnotherBuildBytes));
        try
        {
            var observation = PublishedBundleCatalogue.Read(root, Version);
            Assert.Empty(observation.Artifacts.Modules!.Conflicts);
            Assert.DoesNotContain("Contoso.Sdk", observation.Artifacts.Modules.MvidByModule.Keys);

            var verdict = ReleaseAvailability.IsUpdatable(
                observation.Target, [Required], observation.Artifacts);
            Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── the rule, on hand-built observations ────────────────────────────────────────────────────

    [Fact]
    public void TwoSourcesSealingTwoBuildsOfOneModule_IsInconsistent_ForEveryBundleBindingIt()
    {
        var conflict = $"module {Module}: source 'plugins' sealed mvid:aaaa, source 'socialmedia' sealed mvid:bbbb";
        var artifacts = ReleaseArtifacts.Of([Bundle + ".zip"]) with
        {
            Modules = new SealedModuleSet(
                ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
                [conflict],
                null),
            DependencyRecords = [Record(Module, "mvid:aaaa")],
        };

        var verdict = ReleaseAvailability.IsUpdatable(Target, [Required], artifacts);

        Assert.False(verdict.IsUpdatable);
        var package = Assert.Single(verdict.Packages);
        Assert.Equal(PackageAvailabilityKind.SealedSetInconsistent, package.Kind);
        Assert.Contains(conflict, package.Reason!, StringComparison.Ordinal);
    }

    /// <summary>🚨 The other failure direction. A module set that could not be read is NOT an
    /// incompatibility and NOT a pass — it is "cannot determine", which holds and says so.</summary>
    [Fact]
    public void AnUnreadableModuleSet_IsIndeterminate_NeverAnIncompatibilityVerdict()
    {
        var artifacts = ReleaseArtifacts.Of([Bundle + ".zip"]) with
        {
            Modules = new SealedModuleSet(
                ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
                [],
                "source 'plugins': the publication is sealed but carries no module set"),
            DependencyRecords = [Record(Module, "mvid:aaaa")],
        };

        var verdict = ReleaseAvailability.IsUpdatable(Target, [Required], artifacts);

        Assert.False(verdict.IsUpdatable);
        Assert.True(verdict.IsIndeterminate);
        var package = Assert.Single(verdict.Packages);
        Assert.Equal(PackageAvailabilityKind.Indeterminate, package.Kind);
        Assert.Contains("not clearance to proceed", package.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleSetThatWasNeverObserved_IsIndeterminate_WhenARecordNamesAModule()
    {
        var artifacts = ReleaseArtifacts.Of([Bundle + ".zip"]) with
        {
            Modules = null,
            DependencyRecords = [Record(Module, "mvid:aaaa")],
        };

        var verdict = ReleaseAvailability.IsUpdatable(Target, [Required], artifacts);

        Assert.True(verdict.IsIndeterminate);
        Assert.Equal(PackageAvailabilityKind.Indeterminate, Assert.Single(verdict.Packages).Kind);
    }

    /// <summary>A module the sealed set does not carry is one the instance lands from the registry
    /// outside any publication. The gate cannot see those bytes and does not pretend to.</summary>
    [Fact]
    public void AModuleOutsideTheSealedSet_IsNotJudged()
    {
        var artifacts = ReleaseArtifacts.Of([Bundle + ".zip"]) with
        {
            Modules = new SealedModuleSet(
                ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                    .Add("MeshWeaver.SomethingElse", "mvid:cccc"),
                [],
                null),
            DependencyRecords = [Record(Module, "mvid:aaaa")],
        };

        var verdict = ReleaseAvailability.IsUpdatable(Target, [Required], artifacts);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
    }

    /// <summary>Platform surfaces (<c>ref:</c>) and the reserved toolchain entry are not modules;
    /// a record binding only those needs no module set at all.</summary>
    [Fact]
    public void ARecordBindingNoModule_NeedsNoModuleSet()
    {
        var artifacts = ReleaseArtifacts.Of([Bundle + ".zip"]) with
        {
            Modules = null,
            DependencyRecords =
            [
                new BundleDependencyRecord(Bundle, "Widget/Thing",
                    ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                        .Add("MeshWeaver.Layout", CompiledDependencies.RefAsmScheme + "abc")
                        .Add(CompiledDependencies.ToolchainKey, CompiledDependencies.MvidScheme + "toolchain")),
            ],
        };

        var verdict = ReleaseAvailability.IsUpdatable(Target, [Required], artifacts);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static ReleaseTarget Target => new(Version, Identity);

    private static RequiredPackage Required => new(Bundle, Bundle);

    private static UpdatabilityVerdict Verdict(string root)
    {
        var observation = PublishedBundleCatalogue.Read(root, Version);
        return ReleaseAvailability.IsUpdatable(observation.Target, [Required], observation.Artifacts);
    }

    private static BundleDependencyRecord Record(string module, string id) =>
        new(Bundle, "Widget/Thing",
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(module, id)
                .Add(CompiledDependencies.ToolchainKey, CompiledDependencies.MvidScheme + "toolchain"));

    /// <summary>
    /// One sealed publication under one identity, exactly as <c>publish-bake-bundles.sh</c> lays it
    /// out: the release marker, the module set (index + one module bundle carrying a real assembly),
    /// one content bundle whose manifest records what its NodeType was built against, and the
    /// <c>_complete</c> sentinel written last.
    /// </summary>
    private static string PublishedRoot(string recorded)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-sealed-set-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, Identity, "plugins");
        var modules = Path.Combine(source, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        Directory.CreateDirectory(modules);
        File.WriteAllText(
            Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName, Version), Identity);

        WriteZip(Path.Combine(modules, "probe.module.nupkg"),
            (NuGetPackageWriter.ManifestEntry,
                Encoding.UTF8.GetBytes($$$"""{"plugin":"Probe","module":{"assemblyName":"{{{Module}}}"}}""")),
            ($"{NuGetPackageWriter.ModuleFolder}/{Module}.dll", File.ReadAllBytes(ModuleBytesOf.Location)));
        File.WriteAllText(
            Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName), "probe.module.nupkg\n");

        var manifest = new BundleReader.Manifest(
            Bundle, "1.0", Identity,
            [
                new BundleReader.AssemblyRef("Widget/Thing", "Widget_Thing.dll",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [Module] = recorded,
                        ["MeshWeaver.Layout"] = CompiledDependencies.RefAsmScheme + "abc",
                        [CompiledDependencies.ToolchainKey] = CompiledDependencies.MvidScheme + "toolchain",
                    }),
            ]);
        WriteZip(Path.Combine(source, Bundle + ".zip"),
            (NuGetPackageWriter.ManifestEntry,
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), Bundle + ".zip\n");
        return root;
    }

    /// <summary>
    /// The #3221 layout: ONE identity, ONE source, and an assembly name reaching it from TWO module
    /// bundles — the live shape of MeshWeaver.Plugins, where 19 of 37 bundles carry a copy of an
    /// assembly some other package declares.
    ///
    /// <list type="bullet">
    /// <item><description><c>probe.module.nupkg</c> — the AI-shaped bundle: it DECLARES
    /// <see cref="Module"/> and RIDES <paramref name="riding"/> as <see cref="Sibling"/>.</description></item>
    /// <item><description><c>probe.sibling.nupkg</c> — the Essentials-shaped bundle: it DECLARES
    /// <see cref="Sibling"/> as <paramref name="declared"/>. Omitted entirely when
    /// <paramref name="declared"/> is null, which is the ride-only case.</description></item>
    /// </list>
    ///
    /// The index lists the riding bundle FIRST, so the ride is the copy seen first and the declared
    /// module the second — both roles are exercised in one conflict line.
    /// </summary>
    private static string PublishedRootWithRide(
        string recorded, byte[]? declared, byte[] riding,
        (string Name, byte[] Bytes)? thirdParty = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-sealed-ride-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, Identity, "plugins");
        var modules = Path.Combine(source, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        Directory.CreateDirectory(modules);
        File.WriteAllText(
            Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName, Version), Identity);

        var ridingEntries = new List<(string Entry, byte[] Bytes)>
        {
            (NuGetPackageWriter.ManifestEntry,
                Encoding.UTF8.GetBytes($$$"""{"plugin":"Probe","module":{"assemblyName":"{{{Module}}}"}}""")),
            ($"{NuGetPackageWriter.ModuleFolder}/{Module}.dll", File.ReadAllBytes(ModuleBytesOf.Location)),
            ($"{NuGetPackageWriter.ModuleFolder}/{Sibling}.dll", riding),
        };
        if (thirdParty is { } third)
            ridingEntries.Add(($"{NuGetPackageWriter.ModuleFolder}/{third.Name}.dll", third.Bytes));
        WriteZip(Path.Combine(modules, "probe.module.nupkg"), [.. ridingEntries]);

        var index = new List<string> { "probe.module.nupkg" };
        if (declared is not null)
        {
            var declaredEntries = new List<(string Entry, byte[] Bytes)>
            {
                (NuGetPackageWriter.ManifestEntry,
                    Encoding.UTF8.GetBytes($$$"""{"plugin":"Sibling","module":{"assemblyName":"{{{Sibling}}}"}}""")),
                ($"{NuGetPackageWriter.ModuleFolder}/{Sibling}.dll", declared),
            };
            if (thirdParty is { } alsoThird)
                // The SAME third-party name at the SAME bytes as the entry's other copy would be no
                // test at all — give this one the module's bytes so the two copies genuinely differ.
                declaredEntries.Add((
                    $"{NuGetPackageWriter.ModuleFolder}/{alsoThird.Name}.dll",
                    File.ReadAllBytes(ModuleBytesOf.Location)));
            WriteZip(Path.Combine(modules, "probe.sibling.nupkg"), [.. declaredEntries]);
            index.Add("probe.sibling.nupkg");
        }
        File.WriteAllLines(
            Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName), index);

        var manifest = new BundleReader.Manifest(
            Bundle, "1.0", Identity,
            [
                new BundleReader.AssemblyRef("Widget/Thing", "Widget_Thing.dll",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [Sibling] = recorded,
                        [CompiledDependencies.ToolchainKey] = CompiledDependencies.MvidScheme + "toolchain",
                    }),
            ]);
        WriteZip(Path.Combine(source, Bundle + ".zip"),
            (NuGetPackageWriter.ManifestEntry,
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), Bundle + ".zip\n");
        return root;
    }

    private static void WriteZip(string path, params (string Entry, byte[] Bytes)[] entries)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
    }
}
