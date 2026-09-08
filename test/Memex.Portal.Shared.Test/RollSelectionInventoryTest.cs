#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The roll selector's DENOMINATOR, read off a real mesh (#3479).</b>
///
/// <para><see cref="RollSelectionTest"/> pins the walk against hand-built inputs. This pins the half
/// that decides what those inputs ARE: <c>ReleaseAvailabilityService.SelectRollTarget</c> reading
/// this environment's install records through a real <c>IMeshService</c>, against a real published
/// bundle root on disk. Nothing is mocked, and this is the first test in the suite to drive that
/// service end to end rather than around it.</para>
///
/// <para><b>Why the empty case is the point.</b> The install records are the source the deployment
/// gate already uses, and they have measurably answered ZERO on a live portal that carried 42
/// modules (memex, 2026-08-10 — the measurement that made <c>InstanceComboReader</c> fold two
/// shapes). With an empty denominator every release ships all zero plugins, so a selector that does
/// not refuse the case degenerates into "take the newest" — #3441's vacuity, one level up. Here it
/// is <see cref="RollSelectionKind.NoPluginsKnown"/>, and it selects nothing.</para>
/// </summary>
public class RollSelectionInventoryTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output), IDisposable
{
    private const string CompleteIdentity = "s3479inv0complete0000000000000000";
    private const string CompleteVersion = "3.0.0-rc9.ci.7647";
    private const string IncompleteIdentity = "s3479inv0incomplete00000000000000";
    private const string IncompleteVersion = "3.0.0-rc9.ci.7676";
    private const string RunningVersion = "3.0.0-rc9.ci.7693";

    /// <summary>A slice of the environment's real install records; <c>Feedback</c> is the one the
    /// measured hold named, and the one the newer publication here does not ship.</summary>
    private static readonly string[] Installed = ["AI", "Essentials", "Feedback", "Store"];

    private static readonly string[] ShippedByTheNewerRelease = ["AI", "Essentials", "Store"];

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // 🚨 The default install would write records this test did not ask for, and the
            // denominator is exactly what is under test here.
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// 🚨 THE ALGORITHM, denominator and all: four install records, a newer release that ships three
    /// of them and an older one that ships all four. The service selects the NEWER (#3651 — a
    /// missing bake is a boot compile, not a decline) and the outcome NAMES Feedback as the cost.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task SelectsTheNewestRelease_NamingThePackageItRecompilesAtBoot()
    {
        foreach (var id in Installed)
            await Record(id);

        var root = StagedRoot();

        var outcome = await Service(root)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.Update);
        outcome.SelectedVersion.Should().Be(IncompleteVersion);
        outcome.Declined.Should().BeEmpty();
        outcome.RequiredPlugins.Should().Be(
            Installed.Length,
            "the denominator is the environment's install records, and it is stated rather than "
            + "inferred from a pass");
        outcome.Summary.Should().Contain("install records");
        outcome.BootCompiles.Should().Equal(["Feedback"]);
        outcome.Summary.Should().Contain("would recompile at boot: Feedback");
    }

    /// <summary>
    /// The same fixture on an instance that opts into <c>Modules:RequirePrebuilt</c> — the strict
    /// mode in which the seeder refuses a boot compile — declines the newer NAMING Feedback and
    /// selects the older, exactly as every instance did before #3651. Read from the instance's
    /// own configuration, the same key the seeder reads.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task UnderRequirePrebuilt_SelectsTheLatestReleaseThatShipsEveryInstalledPackage()
    {
        foreach (var id in Installed)
            await Record(id);

        var root = StagedRoot();

        var outcome = await Service(root, requirePrebuilt: true)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.Update);
        outcome.SelectedVersion.Should().Be(CompleteVersion);
        outcome.BootCompiles.Should().BeEmpty();

        var declined = outcome.Declined.Should().ContainSingle().Subject;
        declined.Version.Should().Be(IncompleteVersion);
        declined.Reason.Should().Contain("Feedback");
        declined.Blockers.Should().ContainSingle()
            .Which.Kind.Should().Be(PackageAvailabilityKind.ContentBakeMissing);
    }

    /// <summary>
    /// 🚨 THE VACUITY REFUSAL, on a real mesh: no install records at all. Every candidate would ship
    /// all zero plugins, so nothing is selected and the refusal says the denominator out loud.
    ///
    /// <para>The negative control is inside the assertion: the SAME root, walked with a denominator,
    /// selects the older release (the test above), and the newest is complete-for-nothing here — so
    /// "took the newest" is a state this fixture can actually produce.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task NoInstallRecordsRefusesInsteadOfSelectingTheNewest()
    {
        var root = StagedRoot();

        var outcome = await Service(root)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.NoPluginsKnown);
        outcome.SelectedVersion.Should().BeNull(
            "an empty denominator makes every release vacuously complete — selecting the newest on "
            + "that basis is the defect this refusal exists to prevent");
        outcome.ShouldUpdate.Should().BeFalse();
        outcome.IsIndeterminate.Should().BeTrue();
        outcome.RequiredPlugins.Should().Be(0);
        outcome.Summary.Should().Contain("0 plugins required");

        // 🚨 And the SHARED predicate refuses it too, in the same place, so the poller's own
        // re-gate cannot wave through what the selector declined to choose. A refusal only the
        // selector honours would be a rule with one caller.
        var verdict = await Service(root).IsUpdatable(IncompleteVersion)
            .Should().Within(TestTimeouts.Convergence).Emit();
        verdict.IsUpdatable.Should().BeFalse(
            "a gate that compares two empty sets is the vacuity #3441 removed one level down");
        verdict.IsIndeterminate.Should().BeTrue();
        verdict.HoldReason.Should().Contain("ZERO");
    }

    /// <summary>
    /// With no candidate list the service derives one from the published root's own release markers
    /// — so an environment can answer "which release should I be on" without listing a container
    /// registry it may not be able to reach. Ordering is <c>VersionSelect</c>'s, which is why —
    /// on the strict instance, where the incomplete release is declined — the older-but-complete
    /// release wins over the newer incomplete one rather than over a lexicographic accident.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task DerivesItsCandidatesFromThePublishedReleaseMarkers()
    {
        foreach (var id in Installed)
            await Record(id);

        var root = StagedRoot();

        var outcome = await Service(root, requirePrebuilt: true)
            .SelectRollTarget(RunningVersion)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine(outcome.Summary);

        outcome.SelectedVersion.Should().Be(CompleteVersion);
        outcome.Declined.Select(d => d.Version).Should().Equal([IncompleteVersion]);
    }

    /// <summary>
    /// A configured root that is not there is a mis-mount, not an empty publication history: the
    /// selector HOLDS and names it. Cannot determine is not clearance to take the newest.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnAbsentPublishedRootHoldsRatherThanSelectingTheNewest()
    {
        foreach (var id in Installed)
            await Record(id);

        var missing = Track(Path.Combine(Path.GetTempPath(), "mw-3479-absent-" + Guid.NewGuid().ToString("N")));

        var outcome = await Service(missing)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.Indeterminate);
        outcome.SelectedVersion.Should().BeNull();
        outcome.IsIndeterminate.Should().BeTrue();
        outcome.Summary.Should().Contain("does not exist");
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The service over a root, on an instance that does or does not opt into
    /// <c>Modules:RequirePrebuilt</c> — the same configuration key the seeder reads, on the same
    /// configuration the service reads its published root from.</summary>
    private ReleaseAvailabilityService Service(string publishedRoot, bool requirePrebuilt = false) =>
        new(Mesh,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ShippedPrebuiltBundles.PublishedRootConfigKey] = publishedRoot,
                [DeploymentReportService.DeploymentKey] = "memex",
                [MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder.RequirePrebuiltConfigKey] =
                    requirePrebuilt ? "true" : null,
            }).Build());

    /// <summary>One install record, exactly as <c>PackageInstaller</c> writes it — the same nodes
    /// the service's own query reads back.</summary>
    private Task Record(string packageId) =>
        Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{packageId}") with
                {
                    NodeType = PackageInstaller.PackageNodeType,
                    Name = packageId,
                    State = MeshNodeState.Active,
                    Content = new PackageManifest
                    {
                        Id = packageId,
                        Name = packageId,
                        Version = "1.0.0",
                        TargetPartition = packageId,
                    },
                }))
            .Timeout(TestTimeouts.Convergence).Await();

    /// <summary>Two publications: an older identity that sealed every installed package, a newer one
    /// that did not seal <c>Feedback</c>.</summary>
    private string StagedRoot()
    {
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-3479-inv-" + Guid.NewGuid().ToString("N")));
        var markers = Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, CompleteVersion), CompleteIdentity);
        File.WriteAllText(Path.Combine(markers, IncompleteVersion), IncompleteIdentity);
        Seal(root, CompleteIdentity, Installed);
        Seal(root, IncompleteIdentity, ShippedByTheNewerRelease);
        return root;
    }

    private static void Seal(string root, string identity, IEnumerable<string> bundles)
    {
        var directory = Path.Combine(root, identity, "plugins");
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

    private readonly List<string> roots = [];

    private string Track(string root)
    {
        roots.Add(root);
        return root;
    }

    void IDisposable.Dispose()
    {
        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
