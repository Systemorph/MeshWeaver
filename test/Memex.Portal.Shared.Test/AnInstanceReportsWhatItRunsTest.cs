#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Shared.SelfUpdate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The fleet inventory is fed by the instances themselves: a named instance composes the report the
/// control instance's inbox understands — platform build, commit, framework identity, update policy
/// and one row per module in BOTH recorded shapes — and, being its own control instance here (no
/// Hosting:ReportTo), lands it where a reported one would land, with the discriminator stamped.
/// </summary>
public class AnInstanceReportsWhatItRunsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Deployment = "unit-dep";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static IConfiguration Environment() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentReportService.DeploymentKey] = Deployment,
        }).Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .AddGitHubSyncTypes()
            .AddUpdatePolicyType()
            // The control instance carries the Hosting package, whose ModuleInventory type owns the
            // record a local write lands in; this mesh stands in for that one declaration.
            .AddMeshNodes(new MeshNode(DeploymentReportService.InventoryNodeType)
            {
                Name = "Module Inventory",
                IsSatelliteType = false,
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
            })
            .ConfigureServices(services => services
                .AddSingleton(Environment())
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallPreInstalledPackages = false,
                    InstanceId = "instance-1",
                    HomeUrl = "https://unit.example",
                }));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private DeploymentReportService Reporter => Mesh.ServiceProvider.GetRequiredService<DeploymentReportService>();

    [Fact(Timeout = 180_000)]
    public async Task TheReport_CarriesBothModuleShapesAndThePlatform_AndLandsWhereAReportedOneWould()
    {
        await Seed(new MeshNode(GitHubSyncService.ConfigId, "Course")
        {
            NodeType = GitHubSyncService.ConfigNodeType,
            Name = "Course sync",
            State = MeshNodeState.Active,
            Content = new GitHubSyncConfig
            {
                RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Education",
                Branch = "main",
                Subdirectory = "Course",
                LastSyncCommitSha = Sha,
                LastSyncedAt = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero),
            },
        });
        await Seed(new MeshNode("Sample", PackageInstaller.InstalledPartition)
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = "Sample",
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = "Sample", Name = "Sample", ModuleVersion = "1.2.3" },
        });
        await Seed(new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Stable },
        });

        var outcome = await Reporter.Report().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        outcome.Delivery.Should().Be(DeploymentReportDelivery.Written,
            "without Hosting:ReportTo this instance IS the control instance and files the record itself");
        var report = outcome.Report!;
        report.Event.Should().Be(DeploymentReportService.EventName);
        report.Deployment.Should().Be(Deployment);
        report.InstanceId.Should().Be("instance-1");
        report.Host.Should().Be("https://unit.example");
        report.PlatformVersion.Should().NotBeNullOrWhiteSpace("the standard platform version, never a bespoke key");
        report.FrameworkIdentity.Should().NotBeNullOrWhiteSpace("bundles are keyed on it, so the fleet must see it");
        report.UpdatePolicy.Should().Be(nameof(UpdatePolicyKind.Stable));
        report.SampledAt.Should().EndWith("Z");

        report.Modules.Select(m => m.Id).Should().Equal(new[] { "Course", "Sample" },
            "BOTH shapes, always — an inventory read off one of them looks healthy and reports almost nothing");
        var course = report.Modules.Single(m => m.Id == "Course");
        course.Origin.Should().Be("GitSync");
        course.Repository.Should().Be("https://github.com/Systemorph/MeshWeaver.Education");
        course.Ref.Should().Be("main");
        course.Subdirectory.Should().Be("Course");
        course.CommitSha.Should().Be(Sha);
        course.LastSyncedAt.Should().Be("2026-09-06T08:00:00Z");
        var sample = report.Modules.Single(m => m.Id == "Sample");
        sample.Origin.Should().Be("Package");
        sample.ModuleVersion.Should().Be("1.2.3", "a package-only module reports the installer's version, never an unknown build");

        // The landed node: the same partition a reported one is filed under, and its content
        // MATERIALISES — which is the assertion this test was always reaching for.
        var node = await Read($"{DeploymentReportService.DefaultOperationalSpace}/Modules/{Deployment}");
        node.Should().NotBeNull();
        node!.NodeType.Should().Be(DeploymentReportService.InventoryNodeType);

        // 🚨 READ IT BACK AS THE RECORD, not as raw JSON (#3625). This assertion used to be
        // `content.GetProperty("$type") == InventoryContentType` — a check that the discriminator
        // STRING was stamped, with the reason given as "content without the discriminator is stored
        // perfectly and materialises as NOTHING". The string was stamped and the content
        // materialised as nothing anyway: `"ModuleInventoryContent"` named no CLR type in the
        // fleet, so the reading hub could not resolve it and the value degraded back to a raw
        // JsonElement. The old assertion passed BECAUSE of the defect — raw JSON is the only shape
        // in which a `$type` property is there to be read — which is why it never noticed. Asserting
        // the materialised record instead cannot pass while the type is unregistered.
        // 🚨 The RUNTIME TYPE, not ContentAs<T>. ContentAs is the bad-data-TOLERANT accessor: handed
        // a raw JsonElement it deserialises it anyway, so it answers a DeploymentReport whether or
        // not the discriminator resolved — it cannot fail here, and an assertion that cannot fail is
        // not an assertion. What the defect actually breaks is MATERIALISATION at the read seam:
        // `node.Content is DeploymentReport`, which is what every ordinary reader does and what the
        // stream cache's degradation warning is about.
        node.Content.Should().BeOfType<DeploymentReport>(
            "the inventory node's content must MATERIALISE — an unresolvable discriminator leaves it "
            + "a raw JsonElement, and every reader doing `Content is DeploymentReport` sees nothing");
        var landed = (DeploymentReport)node.Content!;
        landed.Deployment.Should().Be(Deployment);
        landed.Modules.Count.Should().Be(2);
    }

    /// <summary>
    /// The shape this test's first CI run failed on: the service's own boot report read one seeded
    /// module, an explicit report read two, and the boot report's write landed LAST. Every report
    /// now goes through one channel in request order, so the later request — which read the later
    /// state — is what the record says. Under overlapping reports this assertion can fail; under
    /// the channel it cannot.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TwoOverlappingRequests_LandInRequestOrder_SoTheRecordSaysWhatTheInstanceCarriesNow()
    {
        await Seed(new MeshNode(GitHubSyncService.ConfigId, "Course")
        {
            NodeType = GitHubSyncService.ConfigNodeType,
            Name = "Course sync",
            State = MeshNodeState.Active,
            Content = new GitHubSyncConfig { RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Education", Branch = "main" },
        });
        // Request A is enqueued NOW, before the second seed; it may read one module or two.
        var first = Reporter.Report().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        await Seed(new MeshNode("Sample", PackageInstaller.InstalledPartition)
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = "Sample",
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = "Sample", Name = "Sample", ModuleVersion = "1.2.3" },
        });
        // Request B is enqueued after both seeds, so it must read both and its write must land last.
        var second = await Reporter.Report().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        var earlier = await first;

        earlier.Delivery.Should().Be(DeploymentReportDelivery.Written);
        second.Delivery.Should().Be(DeploymentReportDelivery.Written);
        second.Report!.Modules.Select(m => m.Id).Should().Equal(new[] { "Course", "Sample" });

        var node = await Read($"{DeploymentReportService.DefaultOperationalSpace}/Modules/{Deployment}");
        Element(node!.Content!).GetProperty("modules").GetArrayLength().Should().Be(2,
            "the record is the LATER request's write — an earlier read landing last would say what the instance carried a moment ago");
    }

    [Fact]
    public void TheSignature_IsGitHubsWebhookShape()
    {
        // RFC 4231-style vector for HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog").
        DeploymentReportService.Sign("The quick brown fox jumps over the lazy dog", "key")
            .Should().Be("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");
    }

    [Fact]
    public void TheReporter_ReadsThePolicyOffTheNodeMemexDeclares()
    {
        // MeshWeaver.PluginCatalog cannot reference Memex.Portal.Shared, so it carries the node's
        // type and partition as constants; this is what keeps them from drifting apart.
        DeploymentReportService.UpdatePolicyNodeType.Should().Be(UpdatePolicyNodeType.NodeType);
        DeploymentReportService.UpdatePolicyPartition.Should().Be(UpdatePolicyNodeType.AdminPartition);
    }

    private Task Seed(MeshNode node) =>
        Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return MeshService.CreateOrUpdateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private JsonElement Element(object content) =>
        content is JsonElement je
            ? je
            : JsonSerializer.SerializeToElement(content, content.GetType(), Mesh.JsonSerializerOptions);
}

/// <summary>
/// An instance nobody named reports nothing: a report filed under a guessed deployment id would
/// overwrite another instance's inventory, and the fleet page would render that as fact.
/// </summary>
public class AnUnnamedInstanceReportsNothingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new ConfigurationBuilder().Build())
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    [Fact(Timeout = 60_000)]
    public async Task WithoutADeploymentId_TheReportIsSkipped_AndSaysWhichKeyNamesTheInstance()
    {
        var outcome = await Mesh.ServiceProvider.GetRequiredService<DeploymentReportService>()
            .Report().Timeout(TimeSpan.FromSeconds(30)).Await(TestContext.Current.CancellationToken);

        outcome.Delivery.Should().Be(DeploymentReportDelivery.Skipped);
        outcome.Detail.Should().Contain(DeploymentReportService.DeploymentKey);
        outcome.Report.Should().BeNull("nothing was composed, so nothing could have been written or sent");
    }
}
