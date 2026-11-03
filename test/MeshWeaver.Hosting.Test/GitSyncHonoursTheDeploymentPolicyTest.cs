using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The deployment's limits on GitSync (<see cref="GitSyncPolicy"/>): an instance that holds sensitive
/// node content can name the partitions that may sync (<c>GitSync:AllowedPartitions</c>) and switch
/// exports off (<c>GitSync:Export:Enabled</c>). Absent, both keep the shipped behaviour.
/// See <c>Doc/Architecture/SensitiveContentInLogs</c>.
/// </summary>
public class GitSyncPolicyReadingTest
{
    private static GitSyncPolicy Read(params (string Key, string? Value)[] values) =>
        GitSyncPolicy.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build());

    [Fact]
    public void Absent_keys_keep_every_partition_and_export_open()
    {
        var policy = Read();
        Assert.True(policy.ExportEnabled);
        Assert.True(policy.IsPartitionAllowed("AnyPartition/Sub"));
        Assert.Null(policy.ExportRefusal("AnyPartition"));
        Assert.True(GitSyncPolicy.FromConfiguration(null).ExportEnabled);
    }

    [Fact]
    public void The_allow_list_reads_as_a_comma_list_or_an_array_and_matches_by_partition()
    {
        var comma = Read((GitSyncPolicy.AllowedPartitionsKey, "Docs, Modules*"));
        var array = Read(($"{GitSyncPolicy.AllowedPartitionsKey}:0", "Docs"),
            ($"{GitSyncPolicy.AllowedPartitionsKey}:1", "Modules*"));
        foreach (var policy in new[] { comma, array })
        {
            Assert.True(policy.IsPartitionAllowed("docs/_GitSync"), "a partition name matches case-insensitively");
            Assert.True(policy.IsPartitionAllowed("ModulesClaims"), "an entry ending in * matches by prefix");
            Assert.False(policy.IsPartitionAllowed("Patients/_GitSync"));
            Assert.Equal("gitsync.policy.partitionNotAllowed", policy.PartitionRefusal("Patients")!.Key);
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("off")]
    [InlineData("flase")]
    public void Any_export_value_but_true_switches_exports_off(string value)
    {
        var policy = Read((GitSyncPolicy.ExportEnabledKey, value));
        Assert.False(policy.ExportEnabled, "an off switch that was meant to be off must not read as on");
        Assert.Equal("gitsync.policy.exportDisabled", policy.ExportRefusal("Docs")!.Key);
    }

    [Fact]
    public void The_write_guard_recognises_a_sync_source_by_type_or_path()
    {
        Assert.True(GitSyncPartitionWriteGuard.IsSyncSource(new MeshNode("_GitSync", "Docs")));
        Assert.True(GitSyncPartitionWriteGuard.IsSyncSource(new MeshNode("extra", "Docs/_GitSync")));
        Assert.True(GitSyncPartitionWriteGuard.IsSyncSource(
            new MeshNode("x", "Docs/Elsewhere") { NodeType = GitHubSyncService.ConfigNodeType }));
        Assert.False(GitSyncPartitionWriteGuard.IsSyncSource(new MeshNode("Page", "Docs")));
    }
}

/// <summary>
/// The policy, enforced on a live mesh: a sync source outside the allow-list cannot be created, an
/// import into it is refused, and with exports off an allowed partition still cannot export.
/// </summary>
public class GitSyncHonoursTheDeploymentPolicyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/acme/policy-test";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Layer the policy ONTO the host configuration (replacing it would drop every other key).
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [GitSyncPolicy.AllowedPartitionsKey] = "GsAllowed*",
                        [GitSyncPolicy.ExportEnabledKey] = "false",
                    }).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance as IConfiguration;

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private async Task<string> CreateSpace(string prefix)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "GitSync policy",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        return space;
    }

    [Fact(Timeout = 240_000)]
    public async Task A_sync_source_outside_the_allow_list_cannot_be_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var denied = await CreateSpace("GsDenied");
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => Sync
            .SaveConfig(denied, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct));
        Assert.Contains(GitSyncPolicy.AllowedPartitionsKey, refused.ToString());

        var allowed = await CreateSpace("GsAllowed");
        await Sync.SaveConfig(allowed, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var saved = await Sync.WatchConfig(allowed)
            .Where(c => c?.RepositoryUrl == RepoUrl)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(RepoUrl, saved?.RepositoryUrl);
    }

    [Fact(Timeout = 240_000)]
    public async Task An_import_into_a_partition_outside_the_allow_list_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var denied = await CreateSpace("GsDenied");
        var refused = await Assert.ThrowsAsync<GitSyncRefusalException>(() => Sync
            .ReimportAtCommit(denied, "main", "system")
            .Timeout(TestTimeouts.Convergence).Await(ct));
        Assert.Equal("gitsync.policy.partitionNotAllowed", refused.Refusal.Key);

        var newSpace = await Assert.ThrowsAsync<GitSyncRefusalException>(() => Sync
            .ImportFromGitHub(RepoUrl, "main", "GsDenied" + Guid.NewGuid().ToString("N")[..8], "x", null, "system")
            .Timeout(TestTimeouts.Convergence).Await(ct));
        Assert.Equal("gitsync.policy.partitionNotAllowed", newSpace.Refusal.Key);
    }

    [Fact(Timeout = 240_000)]
    public async Task With_exports_off_even_an_allowed_partition_does_not_export()
    {
        var ct = TestContext.Current.CancellationToken;
        var allowed = await CreateSpace("GsAllowed");
        var refused = await Assert.ThrowsAsync<GitSyncRefusalException>(() => Sync
            .SyncToGitHub(allowed, "system")
            .Timeout(TestTimeouts.Convergence).Await(ct));
        Assert.Equal("gitsync.policy.exportDisabled", refused.Refusal.Key);
    }
}
