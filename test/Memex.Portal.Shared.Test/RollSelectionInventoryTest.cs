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
/// not refuse the case degenerates into "take the newest" — #3441's vacuity, one level up. Since
/// 2026-10-11 the platform rolls independently of its packages, so the newest admitted release IS
/// taken — and the outcome says, in the summary and the advisory, that no package was checked.</para>
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
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

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
    /// mode in which the seeder refuses a boot compile. Until 2026-10-11 it declined the newer and
    /// selected the older; since the maintainer decision of that day (<i>"again, we want to roll
    /// platform independently of packages"</i>) it selects the NEWER anyway and names Feedback as
    /// the package that does not follow — it parks under the strict seeder and catches up through
    /// its own update policy.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task UnderRequirePrebuilt_TheNewestIsSelected_AndTheUnpublishedPackageIsAnAdvisory()
    {
        foreach (var id in Installed)
            await Record(id);

        var root = StagedRoot();

        var outcome = await Service(root, requirePrebuilt: true)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.Update);
        outcome.SelectedVersion.Should().Be(IncompleteVersion);
        outcome.Declined.Should().BeEmpty();
        outcome.RequiredPlugins.Should().Be(Installed.Length);
        outcome.SatisfiedPlugins.Should().Be(ShippedByTheNewerRelease.Length);
        outcome.Advisories.Should().NotBeEmpty();
        outcome.Advisories[0].Should().Contain("independently of packages");
        outcome.Advisories[0].Should().Contain("Feedback");
        outcome.Summary.Should().Contain("not following this roll");
    }

    /// <summary>
    /// 🚨 THE VACUITY, on a real mesh: no install records at all. Every candidate would ship all zero
    /// plugins. Since 2026-10-11 that is a statement about PACKAGES and does not stop the platform
    /// (maintainer: <i>"again, we want to roll platform independently of packages"</i>) — the
    /// newest admitted release is selected — but it is still never spelled like "all plugins
    /// present": the summary and the advisory say the denominator out loud and that no package was
    /// checked, and the shared predicate still answers the PACKAGE question with a refusal.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task NoInstallRecordsSelectsTheNewest_AndSaysNoPackageWasChecked()
    {
        var root = StagedRoot();

        var outcome = await Service(root)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.Update);
        outcome.SelectedVersion.Should().Be(IncompleteVersion,
            "the platform rolls independently of packages — an unread denominator is reported, "
            + "never a reason to stay");
        outcome.ShouldUpdate.Should().BeTrue();
        outcome.RequiredPlugins.Should().Be(0);
        outcome.SatisfiedPlugins.Should().Be(0);
        outcome.Summary.Should().Contain("0 plugins required");
        outcome.Summary.Should().Contain("no package was checked");

        // 🚨 The SHARED predicate still answers the PACKAGE question truthfully — a gate that
        // compares two empty sets is the vacuity #3441 removed — and the roll surfaces turn that
        // refusal into an advisory (ForPlatformRoll), never a clearance of the packages.
        var verdict = await Service(root).IsUpdatable(IncompleteVersion)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        verdict.IsUpdatable.Should().BeFalse(
            "a gate that compares two empty sets is the vacuity #3441 removed one level down");
        verdict.IsIndeterminate.Should().BeTrue();
        verdict.HoldReason.Should().Contain("ZERO");
        var roll = verdict.ForPlatformRoll();
        roll.IsUpdatable.Should().BeTrue();
        roll.WaivedHold.Should().Contain("ZERO");
    }

    /// <summary>
    /// With no candidate list the service derives one from the published root's own release markers
    /// — so an environment can answer "which release should I be on" without listing a container
    /// registry it may not be able to reach. Ordering is <c>VersionSelect</c>'s, which is why the
    /// NEWEST marker (ci.7676) is selected rather than a lexicographic accident — and, since
    /// 2026-10-11, selected even on the strict instance where its missing Feedback bake is refused:
    /// that refusal is an advisory on the platform roll.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task DerivesItsCandidatesFromThePublishedReleaseMarkers()
    {
        foreach (var id in Installed)
            await Record(id);

        var root = StagedRoot();

        var outcome = await Service(root, requirePrebuilt: true)
            .SelectRollTarget(RunningVersion)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(outcome.Summary);

        outcome.SelectedVersion.Should().Be(IncompleteVersion);
        outcome.Declined.Should().BeEmpty();
        outcome.Summary.Should().Contain("Feedback");
    }

    /// <summary>
    /// A configured root that is not there is a mis-mount, not an empty publication history. Since
    /// 2026-10-11 that unanswerable PACKAGE question does not hold the platform: with the candidates
    /// in hand the newest admitted one is selected, and the summary and the advisory NAME the
    /// mis-mount — cannot determine is reported, never dressed up as "all plugins present".
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnAbsentPublishedRootSelectsTheNewest_AndNamesTheMisMount()
    {
        foreach (var id in Installed)
            await Record(id);

        var missing = Track(Path.Combine(Path.GetTempPath(), "mw-3479-absent-" + Guid.NewGuid().ToString("N")));

        var outcome = await Service(missing)
            .SelectRollTarget(RunningVersion, [IncompleteVersion, CompleteVersion])
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(outcome.Summary);

        outcome.Kind.Should().Be(RollSelectionKind.Update);
        outcome.SelectedVersion.Should().Be(IncompleteVersion);
        outcome.Summary.Should().Contain("does not exist");
        outcome.Summary.Should().Contain("no package was checked");
        outcome.Advisories.Should().ContainSingle()
            .Which.Should().Contain("does not exist");
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
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

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
