#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The module funnel (#1664 Slices B+C): a package that DECLARES a compiled module routes its
/// binary payload through bundle-fetch → MVID gate → <see cref="ModuleLandingService"/> — never
/// through the file→MeshNode parse — and the declaration itself flows listing → install record so
/// both ends of the funnel (the consumer's adopt and the registry's serve) can key on it.
/// </summary>
public class ModuleFunnelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string landingRoot =
        Path.Combine(Path.GetTempPath(), "mw-funnel-" + Guid.NewGuid().ToString("N"));

    /// <summary>The RUNNING framework identity — what the bundle must carry to be landable.</summary>
    private static string LiveFrameworkMvid =>
        typeof(PrebuiltAssemblySeeder).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return base.ConfigureMesh(builder).AddGraph().AddPluginCatalog()
            // Land into a per-test temp tree, never the test host's own bin folder — the sidecar
            // is a persistent file, and writing it beside the testhost would bleed across tests.
            .ConfigureServices(services =>
                services.AddSingleton(new ModuleLandingService(baseDirectory: landingRoot)));
    }

    private static byte[] ModuleBundle(string frameworkMvid)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SocialMedia",
            version = "1.2.0",
            frameworkMvid,
            module = new
            {
                assemblyName = "MeshWeaver.Social",
                assemblies = new[] { "MeshWeaver.Social.dll" },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest("SocialMedia", "MeshWeaver.Plugin.SocialMedia", "1.2.0", "SocialMedia", null, []),
            "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor("MeshWeaver.Social.dll"),
                    () => new MemoryStream("SOCIAL"u8.ToArray())),
            ],
            manifestJson);
        return buffer.ToArray();
    }

    /// <summary>
    /// The consumer's land half, without HTTP: a bundle carrying the RUNNING framework's MVID lands
    /// into <c>modules/&lt;name&gt;/</c> with its activation entry — version recorded, restart
    /// flagged — which is exactly what boot's activation union then loads.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleForTheRunningFramework_LandsIntoTheModulesTree()
    {
        var client = new PluginBundleClient(Mesh, "http://registry.invalid");

        var landed = await client
            .LandFromBundle("SocialMedia", "MeshWeaver.Social", "Plugins/SocialMedia", "1.2.0",
                ModuleBundle(LiveFrameworkMvid))
            .FirstAsync().ToTask();

        landed.Should().Be(1);
        File.ReadAllBytes(Path.Combine(
                landingRoot, "modules", "MeshWeaver.Social", "MeshWeaver.Social.dll"))
            .Should().Equal("SOCIAL"u8.ToArray());

        var list = ModuleActivationSidecar.Read(landingRoot);
        var entry = list.Entries.Should().ContainSingle().Subject;
        entry.Name.Should().Be("MeshWeaver.Social");
        entry.Version.Should().Be("1.2.0",
            "the recorded version is what lets the reconcile answer 'already landed' without a download");
        entry.PackagePath.Should().Be("Plugins/SocialMedia");
        entry.FrameworkMvid.Should().Be(LiveFrameworkMvid);
        list.PendingRestart.Should().BeTrue("restart-as-activation — nothing loads into the running process");

        Directory.Delete(landingRoot, recursive: true);
    }

    /// <summary>
    /// 🚨 The MVID gate at the client: a bundle built against a DIFFERENT framework is refused
    /// before a byte reaches disk — the refusal is a logged zero, never a failed install, because
    /// the bundle becomes relevant after the next image roll and nothing is wrong today.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleForAForeignFramework_IsRefused_NothingReachesDisk()
    {
        var client = new PluginBundleClient(Mesh, "http://registry.invalid");

        var landed = await client
            .LandFromBundle("SocialMedia", "MeshWeaver.Social", "Plugins/SocialMedia", "1.2.0",
                ModuleBundle(Guid.NewGuid().ToString("N")))
            .FirstAsync().ToTask();

        landed.Should().Be(0);
        Directory.Exists(Path.Combine(landingRoot, "modules", "MeshWeaver.Social"))
            .Should().BeFalse("declined bytes never reach disk");
        ModuleActivationSidecar.Read(landingRoot).Entries.Should().BeEmpty();

        Directory.Delete(landingRoot, recursive: true);
    }

    /// <summary>
    /// The declaration flows onto the install record: the registry's bundle index reads records,
    /// not repos, so a record without the field could never offer the module.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AModuleDeclaringPackage_CarriesTheDeclarationOntoItsInstallRecord()
    {
        var manifest = new PackageManifest
        {
            Id = "SocialMedia",
            Name = "Social Media",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "SocialMedia",
            SourceFolder = "SocialMedia",
            Version = "c1",
            Module = "MeshWeaver.Social",
        };
        IReadOnlyList<PackageFile> files =
        [
            new("SocialMedia/index.json",
                """{"$type":"MeshNode","id":"SocialMedia","namespace":"","path":"SocialMedia","mainNode":"SocialMedia","name":"Social Media","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Posts."}}"""),
            new("SocialMedia/Notes.md", "# Social"),
        ];

        await PackageInstaller.Install(Mesh, manifest, files, "c1").FirstAsync().ToTask();

        var record = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/SocialMedia", Mesh.JsonSerializerOptions)
            .Take(1).ToTask();

        record.Should().NotBeNull();
        record!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.Module
            .Should().Be("MeshWeaver.Social",
                "the record is what the registry's bundle index serves modules from");

        Directory.Delete(landingRoot, recursive: true);
    }

    /// <summary>
    /// The listing side of the same flow: the node-repo source reads the root's
    /// <c>content.module</c> declaration — unread it would be dead metadata, the exact defect
    /// class <c>preInstalled</c>/<c>publicSegments</c>/<c>contactEmail</c> each had.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheNodeRepoListing_ReadsTheModuleDeclaration()
    {
        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(new RepoSnapshot("c1",
            [
                new RepoFile("SocialMedia/index.json",
                    """{"nodeType":"Space","name":"Social Media","content":{"module":"MeshWeaver.Social"}}"""),
                new RepoFile("Plain/index.json",
                    """{"nodeType":"Space","name":"Plain","content":{}}"""),
            ])),
            "https://example.invalid/repo");

        var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();

        packages.Single(p => p.Id == "SocialMedia").Module.Should().Be("MeshWeaver.Social");
        packages.Single(p => p.Id == "Plain").Module.Should().BeNull(
            "a package that declares no module must never enter the module funnel");

        Directory.Delete(landingRoot, recursive: true);
    }
}
