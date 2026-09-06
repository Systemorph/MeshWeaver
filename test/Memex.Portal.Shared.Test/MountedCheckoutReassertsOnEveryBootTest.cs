#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver#3359 — a self-registry <c>memex-local</c> mounts the developer's checkouts and
/// serves packages from them, and the mounted repos are selected by the operator's
/// <c>PluginCatalog:InstallByDefault</c> patterns. That lane SEEDS: it installs once, records the
/// package on <c>Plugins/_DefaultInstallLedger</c>, and never re-asserts it — right for a
/// deployment seeding itself, wrong for a portal whose purpose is to MIRROR a working tree. Measured
/// on 2026-09-05: a portal served a course as of 2026-08-31 while the mount had moved on for a week,
/// every boot logging "0 written, 0 unchanged". Neither auto-update mechanism covers the shape
/// (webhooks a local install never receives; a registry reconciler with no registry).
///
/// <para>The fix is a lane, not a script: a package the operator's patterns select out of a
/// <see cref="ConfiguredPackageSource.LocalCheckout"/> is RECONCILED — exempt from the seed
/// ledger, re-asserted on every boot, at no cost when nothing changed. This fixture is the
/// production decision end to end: a real directory in node-repo format, configured the way
/// <c>memex-local</c> configures a mount (<c>PluginCatalog:Sources:N</c> with a local
/// <c>RepoPath</c>), selected by <c>InstallByDefault</c>, through the very
/// <see cref="InstanceAutoRegistrationService.RunDefaultInstall"/> the boot pass runs.</para>
/// </summary>
public class MountedCheckoutReassertsOnEveryBootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Written BEFORE the mesh is built: a derived class's field initializers run ahead of the base
    // constructor, which is what calls ConfigureMesh.
    private readonly string checkout = MountedRepo.Write("mw-mount-", "A course, as of its first install.");

    private IConfiguration Environment() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Exactly what memex-local writes for a mounted repo: a local RepoPath, node-repo format,
            // the repo's name as the source name.
            ["PluginCatalog:Sources:0:Name"] = "Education",
            ["PluginCatalog:Sources:0:RepoPath"] = checkout,
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
        }).Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(Environment())
                // The baseline is off and no flag is declared: only the operator's pattern over the
                // mount can select anything here, so what re-asserts proves the local-checkout lane
                // and nothing else.
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallPreInstalledPackages = false,
                    InstallByDefault = ["Education/*"],
                }));

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { MountedRepo.Delete(checkout); }
    }

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    [Fact(Timeout = 240_000)]
    public async Task ThePortalKeepsMirroringTheMount_AfterItWasSeededOnce()
    {
        // Boot 1: the seed installs the course and ledgers it — the state every populated
        // self-registry portal sits in.
        var first = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await();
        first.Packages.Should().Equal(new[] { "Course" });
        first.Failed.Should().Be(0);
        (await Read("Course/Lesson"))!.ContentAs<string>(Mesh.JsonSerializerOptions).Should().Be("# Lesson, as of its first install.");

        // The developer keeps working: the lesson changes and a new node appears in the checkout.
        MountedRepo.Write(checkout, "A course, as of its first install.", lesson: "# Lesson, rewritten since.");
        MountedRepo.WriteNode(checkout, "Quiz", "# A quiz that did not exist at the first install.");

        // Boot 2: the same production decision, unchanged configuration. Seeded once, this pass
        // skipped the package on the ledger and the portal kept serving the first install forever —
        // the defect. A mounted checkout is standing operator intent, so it re-asserts.
        var second = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await();
        second.Packages.Should().Equal(new[] { "Course" },
            "a package selected out of a LOCAL checkout is reconciled on every boot, never seeded once (MeshWeaver#3359)");
        second.Failed.Should().Be(0);
        second.Installed.Should().Be(1, "the checkout changed, so the pass wrote it");

        (await Read("Course/Lesson"))!.ContentAs<string>(Mesh.JsonSerializerOptions).Should().Be("# Lesson, rewritten since.");
        (await Read("Course/Quiz")).Should().NotBeNull("a node added to the checkout after the first install reaches the portal");

        // Boot 3: nothing changed in the checkout — the reconcile is one listing and no writes.
        var third = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await();
        third.Packages.Should().Equal(new[] { "Course" });
        third.Installed.Should().Be(0, "the content-hash gate turns an unchanged mount into a no-op");
        third.UpToDate.Should().Be(1);
    }

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).Await();
}

/// <summary>
/// The control for <see cref="MountedCheckoutReassertsOnEveryBootTest"/>: the SAME patterns over a
/// source that is NOT a local checkout keep seeding once. A registered <see cref="IPackageSource"/>
/// (the shape a remote registry or a DI-provided source takes — no <c>RepoPath</c>, so never a
/// checkout) is installed on boot 1, ledgered, and left alone on boot 2 even though its content
/// moved. That is the seed contract every deployed portal relies on ("the seed must not fight an
/// operator"), and the local-checkout lane must not widen it.
/// </summary>
public class FetchedSourceStillSeedsOnceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string checkout = MountedRepo.Write("mw-fetched-", "A course, fetched.");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // A DI-registered source is named `registered-0` by the service and carries no
                // RepoPath: the fetched-source shape, whatever it reads from — here the same files,
                // so the only difference between the two fixtures is the source's kind.
                .AddSingleton<IPackageSource>(_ => new NodeRepoPackageSource(
                    (_, _, _, _) => MountedRepo.Snapshot(checkout), "https://example.invalid/education"))
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallPreInstalledPackages = false,
                    InstallByDefault = ["registered-0/*"],
                }));

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { MountedRepo.Delete(checkout); }
    }

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    [Fact(Timeout = 240_000)]
    public async Task ASeededPackageFromAFetchedSource_IsNotReasserted()
    {
        var first = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await();
        first.Packages.Should().Equal(new[] { "Course" });

        MountedRepo.Write(checkout, "A course, fetched.", lesson: "# Lesson, rewritten since.");

        var second = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await();
        second.Packages.Should().BeEmpty(
            "a fetched source's package is a SEED: ledgered on the first boot and never re-asserted, "
            + "so an operator who removes it is not fought by the next restart");

        (await Read("Course/Lesson"))!.ContentAs<string>(Mesh.JsonSerializerOptions).Should().Be("# Lesson, fetched.",
            "the seed did not touch it again");
    }

    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).Await();
}

/// <summary>One package, <c>Course</c>, in node-repo format on disk — the shape memex-local mounts.</summary>
internal static class MountedRepo
{
    private const string Root =
        """
        {"$type":"MeshNode","id":"Course","namespace":"","path":"Course","mainNode":"Course",
         "name":"Course","nodeType":"Space","state":"Active",
         "content":{"$type":"PluginManifest","description":"DESCRIPTION","minMeshVersion":"1.0.0"}}
        """;

    private const string Node =
        """
        {"$type":"MeshNode","id":"ID","namespace":"Course","path":"Course/ID","mainNode":"Course/ID",
         "name":"ID","nodeType":"Markdown","state":"Active","content":"CONTENT"}
        """;

    public static string Write(string prefix, string description)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Write(root, description, lesson: "# Lesson, " + description.Split(", ")[1]);
        return root;
    }

    /// <summary>(Re)writes the package root and its lesson; a longer lesson changes the
    /// directory's content hash, which is what the mirror has to notice.</summary>
    public static void Write(string root, string description, string lesson)
    {
        Directory.CreateDirectory(Path.Combine(root, "Course"));
        File.WriteAllText(Path.Combine(root, "Course", "index.json"),
            Root.Replace("DESCRIPTION", description, StringComparison.Ordinal));
        WriteNode(root, "Lesson", lesson);
    }

    public static void WriteNode(string root, string id, string content) =>
        File.WriteAllText(Path.Combine(root, "Course", id + ".json"),
            Node.Replace("ID", id, StringComparison.Ordinal).Replace("CONTENT", content, StringComparison.Ordinal));

    /// <summary>The directory as a fetched snapshot — what a remote source would answer.</summary>
    public static IObservable<MeshWeaver.GitSync.RepoSnapshot> Snapshot(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new MeshWeaver.GitSync.RepoFile(
                Path.GetRelativePath(root, p).Replace('\\', '/'), File.ReadAllText(p)))
            .ToList();
        // The snapshot id is a hash of paths AND contents, so any change — even one that keeps a
        // file's length — is a new snapshot.
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", files.Select(f => f.Path + "\0" + f.Content)))));
        return Observable.Return(new MeshWeaver.GitSync.RepoSnapshot(sha, files));
    }

    public static void Delete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
