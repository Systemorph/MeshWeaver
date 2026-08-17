#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The install record's <see cref="PackageManifest.Source"/> — the registry source a package was
/// installed FROM — must SURVIVE a re-install (#1772).
///
/// <para>Since #1772 the bundle route matches a consuming instance's <c>PluginGrant</c> against
/// exactly this field, so it stopped being a provenance nicety and became an authorization input:
/// a record with no source matches no grant entry and is servable to nobody. Fail-closed is
/// deliberate — but only for a source that was never recorded, never for one an update threw away.</para>
///
/// <para>The erasure is easy to reach and completely silent. Not every lister stamps the field: the
/// registry stamps it as it merges its sources and the default install's own lister stamps it, but a
/// catalog rendered straight off a repo path (<c>PluginUpdateWatcher</c>, a <c>PluginCatalog</c>
/// node) hands over a manifest with none. The record is rebuilt from that manifest, so without the
/// carry-forward the first unattended update would take the whole distribution lane dark — every
/// consumer would just quietly compile instead, which looks like nothing at all.</para>
/// </summary>
public class InstallRecordSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PackageId = "SourceCarryForward";
    private const string RegistrySource = "Plugins";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private static PackageManifest Manifest(string? source) => new()
    {
        Id = PackageId,
        Name = PackageId,
        Kind = PackageKind.Content,
        TargetPartition = PackageId,
        SourceFolder = PackageId,
        Version = "1.0.0",
        ReleasedVersion = "1.4.0",
        Source = source,
    };

    private Task<InstallResult> Install(PackageManifest manifest) =>
        PackageInstaller.Install(
                Mesh, manifest, [new PackageFile($"{PackageId}/Doc.md", $"# {PackageId}")], "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

    /// <summary>Authoritative single-node read straight off storage — never the lagging index.</summary>
    private Task<MeshNode?> ReadRecord() =>
        Mesh.ServiceProvider.GetRequiredService<Mesh.Services.IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{PackageId}", Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

    [Fact(Timeout = 300_000)]
    public async Task ASourcelessReinstall_KeepsTheRecordedSource()
    {
        await Install(Manifest(RegistrySource));
        var installed = await ReadRecord();
        installed!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.Source
            .Should().Be(RegistrySource, "the install must record where the package came from");

        // The unattended update: the same package, from a catalog entry that carries no source.
        await Install(Manifest(null));

        var updated = await ReadRecord();
        updated!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.Source
            .Should().Be(RegistrySource,
                "an update rebuilt from a source-less catalog manifest must not erase the source the "
                + "bundle route's grant check is matched against");
    }

    /// <summary>The rule on its own: it can only FILL IN, never change or invent — so it cannot
    /// widen what any grant matches.</summary>
    [Fact(Timeout = 30_000)]
    public void TheCarryForwardIsMonotone()
    {
        PackageInstaller.SeedSource(Manifest(RegistrySource), Manifest(null))
            .Should().Be(RegistrySource, "a source-less manifest keeps what was recorded");
        PackageInstaller.SeedSource(Manifest(RegistrySource), Manifest("Education"))
            .Should().Be("Education", "a stated source for THIS install wins — it is the newer fact");
        PackageInstaller.SeedSource(null, Manifest(null))
            .Should().BeNull("nothing to carry forward, and nothing is invented: the gate fails closed");
    }
}
