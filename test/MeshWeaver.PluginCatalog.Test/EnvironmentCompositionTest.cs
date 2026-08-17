#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE ACCEPTANCE CRITERION, end to end: "<c>memex</c> lists all of Plugins; <c>systemorph</c> the
/// same, without the games and fun stuff."
///
/// <para>🚨 <c>PluginCatalog:InstallByDefault</c> cannot express that, and this fixture proves it
/// rather than asserting it in a comment: both real portals are long-populated and the seed is
/// ledger-gated, so setting it there is a no-op. The feature-flag lane RE-ASSERTS on every boot —
/// that is the whole difference — and a declared-but-DISABLED flag subtracts, which is how "without
/// the games" is ONE line per environment on top of ONE shared declaration.</para>
///
/// <para>The configuration below is written the way an operator's values file is: a real source
/// NAME (<c>Plugins</c>) over a real repo, through <c>PluginCatalog:Sources</c>, so the
/// source-scoped matching that the whole design leans on is genuinely exercised — a
/// DI-registered stub source is named <c>registered-N</c> and would quietly sidestep it.</para>
/// </summary>
public class EnvironmentCompositionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly IReadOnlySet<string> DbServed =
        new HashSet<string>(StringComparer.Ordinal) { "Reports", "Chess", "ThreeBody" };

    // Written BEFORE the mesh is built: a derived class's field initializers run ahead of the base
    // constructor, which is what calls ConfigureMesh.
    private readonly string repoRoot = WriteRepo();

    /// <summary>
    /// The environment, as it reaches a pod: the source it reads, one SHARED declaration of both
    /// flags, and the single line that turns the games off here — the <c>systemorph</c> shape.
    /// </summary>
    private IConfiguration Environment() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginCatalog:Sources:0:Name"] = "Plugins",
            ["PluginCatalog:Sources:0:RepoPath"] = repoRoot,
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
            ["Features:Flags:plugins:Packages:0"] = "Plugins/*",
            ["Features:Flags:games:Packages:0"] = "Plugins/Chess",
            ["Features:Flags:games:Packages:1"] = "Plugins/ThreeBody",
            ["Features:Flags:games:Enabled"] = "false",
        }).Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddAgentType(DbServed)
            .AddSkillType(DbServed)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // Last registration wins — the mesh now reads the environment we are simulating.
                .AddSingleton(Environment())
                // The platform baseline is off, so NOTHING but the flags can select a package here:
                // an install proves the flag lane, never a preInstalled declaration.
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    /// <summary>Three packages, no <c>preInstalled</c> anywhere: what lands is decided by the
    /// environment alone.</summary>
    private static string WriteRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-compose-" + Guid.NewGuid().ToString("N")[..8]);
        Write(root, "Reports", "Serious business reporting.");
        Write(root, "Chess", "A game.");
        Write(root, "ThreeBody", "A simulation toy.");
        return root;
    }

    private const string RootTemplate =
        """
        {"$type":"MeshNode","id":"ID","namespace":"","path":"ID","mainNode":"ID",
         "name":"ID","nodeType":"Space","state":"Active",
         "content":{"$type":"PluginManifest","description":"DESCRIPTION"}}
        """;

    private static void Write(string root, string id, string description)
    {
        Directory.CreateDirectory(Path.Combine(root, id));
        File.WriteAllText(
            Path.Combine(root, id, "index.json"),
            RootTemplate.Replace("ID", id, StringComparison.Ordinal)
                .Replace("DESCRIPTION", description, StringComparison.Ordinal));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(repoRoot, recursive: true); } catch { /* best effort */ }
    }

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    [Fact(Timeout = 240_000)]
    public async Task AllOfPlugins_WithoutTheGames()
    {
        var summary = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).ToTask();

        // The enabled `plugins` flag selects the whole source; the disabled `games` flag subtracts
        // the two it names.
        summary.Packages.Should().Equal(new[] { "Reports" });
        summary.Failed.Should().Be(0);

        (await Read("Reports")).Should().NotBeNull();
        (await Read("Chess")).Should().BeNull("a disabled flag EXCLUDES the packages it names");
        (await Read("ThreeBody")).Should().BeNull();
        (await Read($"{PackageInstaller.InstalledPartition}/Chess")).Should().BeNull(
            "an excluded package must not even get an install record");
    }

    [Fact(Timeout = 240_000)]
    public async Task TheFlagLaneRECONCILES_UnlikeTheSeedOnceKnob()
    {
        // Wait for the boot pass, so the instance is POPULATED and the seed ledger is written — the
        // exact state in which InstallByDefault becomes a no-op and both live portals already sit.
        await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).ToTask();

        // Now run the PRODUCTION decision again, unchanged. A seed would select nothing here.
        var second = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        // A per-environment policy RE-ASSERTS: that is what makes it able to say "this environment
        // always has X" on an already-populated portal, where the seed can say nothing at all.
        second.Packages.Should().Equal(new[] { "Reports" });
        second.Failed.Should().Be(0);
        second.Installed.Should().Be(0,
            "and it costs nothing when nothing changed — the content-identity gate turns the "
            + "reconcile into one listing and no writes");
        second.UpToDate.Should().Be(1);
    }

    [Fact]
    public void TheEXCLUSIONWinsOverEverySelectionSignal()
    {
        // Pure, on the same decision the boot pass uses: "this environment does not have that" is an
        // explicit statement, so it outranks the platform's own preInstalled baseline and the
        // operator's seed alike. Without this, "all of Plugins WITHOUT the games" would silently
        // start failing the day a game declared preInstalled.
        var excluded = InstanceAutoRegistrationService.Parse(
            [new FeaturePackage("games", "Plugins/Chess")]);

        InstanceAutoRegistrationService.ExcludedBy(excluded, new PackageManifest
        { Id = "Chess", Source = "Plugins", PreInstalled = true }).Should().Be("games");
        InstanceAutoRegistrationService.ExcludedBy(excluded, new PackageManifest
        { Id = "Reports", Source = "Plugins" }).Should().BeNull();
        // Source-scoped, exactly like every other pattern here: a same-named package from another
        // repo is a different package.
        InstanceAutoRegistrationService.ExcludedBy(excluded, new PackageManifest
        { Id = "Chess", Source = "Education" }).Should().BeNull();
    }

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();
}
