#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The parameter gate PROVEN BY ITS EFFECT, on the real boot pass.
///
/// <para>Two packages, identical but for the parameter each declares: <c>Reports</c> needs a
/// connection string this environment DOES supply, <c>Warehouse</c> one it does NOT. The environment
/// asks for both. Afterwards exactly one is installed and the other is reported FAILED — never
/// installed half-configured, and never quietly skipped.</para>
///
/// <para>🚨 The "never quietly skipped" half is the one that needs a test. A gate that suppressed the
/// install and reported success would be indistinguishable, from every surface, from a gate that
/// never ran — the trapdoor shape AGENTS.md forbids — so the assertion is on
/// <c>DefaultInstallSummary.Failed</c>, not merely on the absent content.</para>
/// </summary>
public class PackageParameterGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly IReadOnlySet<string> DbServed =
        new HashSet<string>(StringComparer.Ordinal) { "Reports", "Warehouse" };

    private readonly string repoRoot = WriteRepo();

    /// <summary>The environment: it declares it wants both packages, and supplies ONE of the two
    /// connection strings they need.</summary>
    private IConfiguration Environment() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginCatalog:Sources:0:Name"] = "Plugins",
            ["PluginCatalog:Sources:0:RepoPath"] = repoRoot,
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
            ["Features:Flags:analytics:Packages:0"] = "Plugins/*",
            // The Aspire-injected name — ConnectionStrings__reporting on a container.
            ["ConnectionStrings:reporting"] = "Host=pg;Database=reporting",
        }).Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddAgentType(DbServed)
            .AddSkillType(DbServed)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(Environment())
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    private static string WriteRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-params-" + Guid.NewGuid().ToString("N")[..8]);
        Write(root, "Reports", "reporting", "The reporting database.");
        Write(root, "Warehouse", "warehouse", "The finance warehouse.");
        return root;
    }

    private const string RootTemplate =
        """
        {"$type":"MeshNode","id":"ID","namespace":"","path":"ID","mainNode":"ID",
         "name":"ID","nodeType":"Space","state":"Active",
         "content":{"$type":"PluginManifest","description":"DESCRIPTION",
                    "parameters":[{"name":"PARAM","kind":"ConnectionString",
                                   "description":"DESCRIPTION"}]}}
        """;

    private static void Write(string root, string id, string parameter, string description)
    {
        Directory.CreateDirectory(Path.Combine(root, id));
        File.WriteAllText(
            Path.Combine(root, id, "index.json"),
            RootTemplate.Replace("ID", id, StringComparison.Ordinal)
                .Replace("PARAM", parameter, StringComparison.Ordinal)
                .Replace("DESCRIPTION", description, StringComparison.Ordinal));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(repoRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact(Timeout = 240_000)]
    public async Task AMissingRequiredParameterFAILSTheInstall_ItDoesNotSkipIt()
    {
        var summary = await Mesh.ServiceProvider
            .GetRequiredService<InstanceAutoRegistrationService>()
            .Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).ToTask();

        // Both were SELECTED — the refusal is about provisioning, not selection.
        summary.Packages.Should().Contain(["Reports", "Warehouse"]);
        summary.Failed.Should().Be(1,
            "a package whose required parameter is unsupplied must be reported FAILED; reporting it "
            + "as success or omitting it would make 'the gate never ran' and 'the gate passed' "
            + "indistinguishable");
        summary.Installed.Should().Be(1);

        (await Read("Reports")).Should().NotBeNull(
            "one package's missing parameter must not withhold the others");
        (await Read("Warehouse")).Should().BeNull(
            "never a half-configured install — no content, no record");
        (await Read($"{PackageInstaller.InstalledPartition}/Warehouse")).Should().BeNull();
    }

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();
}
