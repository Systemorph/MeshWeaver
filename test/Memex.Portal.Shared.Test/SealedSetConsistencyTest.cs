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

    private static string SealedMvid =>
        CompiledDependencies.MvidScheme + ModuleBytesOf.ManifestModule.ModuleVersionId.ToString("N");

    private static string AnotherBuild =>
        CompiledDependencies.MvidScheme + Guid.NewGuid().ToString("N");

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
