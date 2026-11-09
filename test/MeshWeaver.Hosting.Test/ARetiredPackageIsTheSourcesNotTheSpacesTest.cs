using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Which package a sync source IS — the source's, never merely its Space's</b> (review on
/// #8053, Systemorph/MeshWeaver#8020). Pure: the rule <see cref="RetiredPackageSyncGate.CoveredBy"/>
/// applies, with a negative control for each way it could over-match.
/// </summary>
public class ARetiredPackageIsTheSourcesNotTheSpacesTest
{
    private const string PluginsRepo = "https://github.com/acme/plugins";
    private const string OtherRepo = "https://github.com/acme/elsewhere";

    private static readonly ImmutableList<FeaturePackage> Retired = [new("fleetops", "Plugins/Hosting")];

    private static string? SourceNameOf(string? repo)
        => RetiredPackageSyncGate.SameRepository(repo, PluginsRepo) ? "Plugins" : null;

    private static GitHubSyncConfig Source(string repo, string? subdirectory)
        => new() { RepositoryUrl = repo, Branch = "main", Subdirectory = subdirectory };

    [Fact]
    public void ThePrimarySourceOfTheRetiredPackage_IsCovered()
    {
        RetiredPackageSyncGate.CoveredBy("Hosting", primary: true, Source(PluginsRepo, "Hosting"), Retired, SourceNameOf)
            .Should().NotBeNull("the package install's own source — Plugins/Hosting — is what the flag retires");
        RetiredPackageSyncGate.CoveredBy("Hosting", primary: true, Source(PluginsRepo, "src/HostingFolder"), Retired, SourceNameOf)
            .Should().NotBeNull("the primary source's Space is the package's target partition, whatever its folder is called");
    }

    [Fact]
    public void AnotherSourceOfTheSameSpace_IsNotCovered_UnlessItIsThePackageFolder()
    {
        RetiredPackageSyncGate.CoveredBy("Hosting", primary: false, Source(PluginsRepo, "Other"), Retired, SourceNameOf)
            .Should().BeNull("an ADDITIONAL source of the Space mirrors another folder — retiring the Space's "
                + "package says nothing about it, so it keeps syncing");
        RetiredPackageSyncGate.CoveredBy("Hosting", primary: false, Source(OtherRepo, "Hosting"), Retired, SourceNameOf)
            .Should().BeNull("a source of an UNRELATED repository is no package of the retired source");
        RetiredPackageSyncGate.CoveredBy("Hosting", primary: true, Source(OtherRepo, null), Retired, SourceNameOf)
            .Should().BeNull("even a primary source: an unrelated repository is no package of the Plugins source");
        RetiredPackageSyncGate.CoveredBy("Hosting", primary: false, Source(PluginsRepo, "Hosting"), Retired, SourceNameOf)
            .Should().NotBeNull("control: an additional source that IS the retired package's folder is covered");
    }

    [Fact]
    public void APrimaryPathEndsInTheConfigSegment()
    {
        RetiredPackageSyncGate.IsPrimary($"Hosting/{GitHubSyncService.ConfigId}").Should().BeTrue();
        RetiredPackageSyncGate.IsPrimary($"Hosting/{GitHubSyncService.ConfigId}/extra").Should().BeFalse();
    }
}

/// <summary>
/// 🚨 <b>The seal reconciler NAMES every source it leaves alone because its package is retired</b>
/// (review on #8053; the #1326 rule the push fan-out already follows). A seal of the repository two
/// Spaces sync arrives; one Space's package is declared retired. The pass's census line names the
/// retired one with its flag. NEGATIVE CONTROL: the Space the pass did consider is not in that line.
/// </summary>
public class ASealPassNamesTheRetiredSourcesItSkipsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/seal-retired";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string SealedSha = "5ea15ea15ea15ea15ea15ea15ea15ea15ea15ea1";

    private readonly ASyncedPartitionDoesNotGrowWithoutBoundTest.ScriptedRepoClient repoClient = new();
    private readonly ConcurrentQueue<string> logs = new();
    private readonly string retiredSpace = "SealRetired" + Guid.NewGuid().ToString("N")[..8];
    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-seal-retired-" + Guid.NewGuid().ToString("N")[..12]);

    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var sourceDirectory = Path.Combine(publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, "plugins");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, SealedPublicationIndex.RepositoryMarkerFileName), RepoFullName);
        File.WriteAllText(Path.Combine(sourceDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), SealedSha);
        File.WriteAllText(Path.Combine(sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Empty);
        return base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                // The census is an Information line; the host's filter may sit above that, so this
                // provider gets its own rule (a provider-specific rule wins over the default).
                services.AddLogging(logging => logging
                    .AddFilter<QueueLoggerProvider>(null, LogLevel.Information)
                    .Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logs)));
                // Layer onto the host configuration (see HeldSourceSaysItIsHeldTest).
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (configured?.ImplementationFactory is { } factory && factory(sp) is IConfiguration host)
                        configuration.AddConfiguration(host);
                    else if (configured?.ImplementationInstance is IConfiguration instance)
                        configuration.AddConfiguration(instance);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ShippedPrebuiltBundles.PublishedRootConfigKey] = publishedRoot,
                        ["PluginCatalog:Sources:0:Name"] = "Plugins",
                        ["PluginCatalog:Sources:0:RepoPath"] = RepoUrl,
                        ["Features:Flags:fleetops:Enabled"] = "false",
                        ["Features:Flags:fleetops:Packages:0"] = $"Plugins/{retiredSpace}",
                    }).Build();
                });
            });
    }

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    [Fact(Timeout = 300_000)]
    public async Task ARetiredSource_IsNamedInTheSealPassCensus_AndNotDispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        repoClient.Stage(SealedSha,
            [new RepoFile("Lesson.md", "---\nNodeType: Markdown\nName: Lesson\n---\n\nLesson.\n")]);
        var kept = "SealKept" + Guid.NewGuid().ToString("N")[..8];
        foreach (var space in new[] { retiredSpace, kept })
            await ArrangeSpace(space, ct);
        foreach (var space in new[] { retiredSpace, kept })
            await QueryShows(space, ct);

        var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        var sealedForThisIdentity = SealedPublicationIndex.ReadFor(publishedRoot, identity);
        sealedForThisIdentity.Should().ContainSingle(s => s.IsSealed, "the pass must be asked about the staged seal");
        var dispatched = await Mesh.ServiceProvider.GetRequiredService<IPublicationSyncReconciler>()
            .Reconcile(identity, sealedForThisIdentity, [])
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        var census = logs.Where(l => l.Contains("[SealedSync]") && l.Contains("not considered")).ToList();
        foreach (var line in census)
            Output.WriteLine(line);
        dispatched.Should().Be(0,
            "the seal never chooses a source's commit for a source with none yet (policy "
            + "module-sync-per-manifest-hash) — so the census line below is the only trace of the retired skip");
        census.Should().ContainSingle("the pass names what it left alone, once")
            .Which.Should().Contain($"{retiredSpace}/{GitHubSyncService.ConfigId}")
            .And.Contain("retired by feature flag 'fleetops'")
            .And.NotContain(kept, "control: the Space the pass did consider is not reported as skipped");
    }

    private async Task ArrangeSpace(string space, CancellationToken ct)
    {
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Seal census",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var owner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        var credentials = Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
        foreach (var identity in new[] { owner, UserId }.Distinct())
            await credentials
                .Save(identity, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private Task QueryShows(string space, CancellationToken ct)
        => Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Where(c => c.Items.Any(n => n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)?.RepositoryUrl == RepoUrl))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <inheritdoc />
    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(publishedRoot))
                Directory.Delete(publishedRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run costs nothing and must never fail a test.
        }
        base.Dispose();
    }

    internal sealed class QueueLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(sink);

        public void Dispose() { }

        private sealed class QueueLogger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Enqueue(formatter(state, exception));
        }
    }
}
