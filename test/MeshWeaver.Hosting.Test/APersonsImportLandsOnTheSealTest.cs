using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A person's import reads EXACTLY what was asked — the seal no longer redirects or holds
/// it</b> (policy <c>module-sync-per-manifest-hash</c>; this used to pin the redirect onto the sealed
/// commit, MeshWeaver#3845 hole 3).
///
/// <para><b>What is measured.</b> The ref that reaches <see cref="IGitHubRepoClient.Fetch"/> — not a
/// log line and not a decision function. Over the same three publications that used to redirect or
/// hold (sealed, torn, another repository's), the fetch receives what the person asked for, and the
/// activity carries no seal line. Whether each module then writes anything is judged inside the
/// import by its manifest hash (<c>ModuleSyncDecisionTest</c>), and whether each NodeType adopts
/// bytes or compiles is decided per type.</para>
///
/// <para>One seam is substituted, the GitHub transport, exactly as in
/// <c>HeldSourceSaysItIsHeldTest</c>. The published bundle root is a REAL directory carrying the
/// markers <c>publish-bake-bundles.sh</c> writes, read by the real index on the real FileSystem pool.
/// Everything between is the real mesh.</para>
/// </summary>
public class APersonsImportLandsOnTheSealTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/persons-import";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";

    /// <summary>The bake source directory the seal lives under — the name the Plugins lane uses.</summary>
    private const string SealedSourceName = "plugins";

    /// <summary>The commit this instance's publication of the repository was baked from.</summary>
    private const string SealedSha = "24c2d024b1f25e84a9c98525cfb543d3cbb79fda";

    private readonly RecordingRepoClient repoClient = new();

    /// <summary>The published bundle root this test stages; removed on dispose.</summary>
    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-persons-import-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The DevLogin user the test base logs in.</summary>
    private static string UserId => TestUsers.Admin.ObjectId!;

    private string SourceDirectory => Path.Combine(
        publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, SealedSourceName);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the recording client replaces the git/Octokit transport.
                services.AddSingleton<IGitHubRepoClient>(repoClient);

                // 🚨 LAYER the published-bundle root ONTO the host's configuration, never replace it
                // — see HeldSourceSaysItIsHeldTest for what replacing it cost.
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
                        [ShippedPrebuiltBundles.PublishedRootConfigKey] = publishedRoot,
                    }).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it — update this hook rather than replacing the host's configuration.");

    /// <summary>A publication of <paramref name="repository"/> at <see cref="SealedSha"/>, sealed
    /// (completion sentinel present) or torn (sentinel absent).</summary>
    private void StagePublication(string repository, bool sealedState)
    {
        Directory.CreateDirectory(SourceDirectory);
        File.WriteAllText(Path.Combine(SourceDirectory, SealedPublicationIndex.RepositoryMarkerFileName), repository);
        File.WriteAllText(Path.Combine(SourceDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), SealedSha);
        if (sealedState)
            File.WriteAllText(Path.Combine(SourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Empty);
    }

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 120_000)]
    public async Task UpdateToLatest_OfASealedRepository_ReadsTheBranch_AsAsked()
    {
        StagePublication(RepoFullName, sealedState: true);
        var space = await ArmedSpace("Update", TestContext.Current.CancellationToken);

        var log = await Run(Mesh.UpdateToLatestFromGitHub(space, UserId), TestContext.Current.CancellationToken);

        // The activity is terminal and the import runs INSIDE it, so every fetch it made is already
        // recorded: this list is complete, not a window.
        repoClient.Requested.Should().Equal(["main"],
            "policy module-sync-per-manifest-hash: the seal never chooses a source's commit — a person's "
            + "Update reads the branch, and each module is then judged by its manifest hash");
        log.Messages.Select(m => m.MessageKey)
            .Should().NotContain(k => k != null && k.StartsWith("activity.gitsync.seal.", StringComparison.Ordinal),
                "an import that runs exactly as asked has nothing about a seal to say");
    }

    [Fact(Timeout = 120_000)]
    public async Task ReimportAtATypedBranch_OfASealedRepository_FetchesWhatWasTyped()
    {
        StagePublication(RepoFullName, sealedState: true);
        var space = await ArmedSpace("Reimport", TestContext.Current.CancellationToken);

        await Run(Mesh.ReimportFromGitHub(space, "main", UserId), TestContext.Current.CancellationToken);

        repoClient.Requested.Should().Equal(["main"], "the re-import reads exactly the typed ref");
    }

    [Fact(Timeout = 120_000)]
    public async Task UpdateToLatest_AgainstATornPublication_StillReadsTheBranch()
    {
        StagePublication(RepoFullName, sealedState: false);
        var space = await ArmedSpace("Torn", TestContext.Current.CancellationToken);

        var log = await Run(Mesh.UpdateToLatestFromGitHub(space, UserId), TestContext.Current.CancellationToken);

        repoClient.Requested.Should().Equal(["main"],
            "a torn publication means no bytes are adopted — it never stops the sources from arriving");
        log.Messages.Select(m => m.MessageKey)
            .Should().NotContain("activity.gitsync.seal.heldNotSealed", "nothing is held any more");
    }

    [Fact(Timeout = 120_000)]
    public async Task UpdateToLatest_OfARepositoryThisInstanceRunsNoPublicationOf_StillReadsTheBranch()
    {
        // The control: a seal of ANOTHER repository says nothing about this one.
        StagePublication("test/some-other-repository", sealedState: true);
        var space = await ArmedSpace("Unattributable", TestContext.Current.CancellationToken);

        var log = await Run(Mesh.UpdateToLatestFromGitHub(space, UserId), TestContext.Current.CancellationToken);

        repoClient.Requested.Should().Equal(["main"],
            "hole 1's adjudication: a repository no lane publishes could never be released from a hold, so a "
            + "person's Update of it reads the branch as before");
        log.Messages.Select(m => m.MessageKey)
            .Should().NotContain(k => k != null && k.StartsWith("activity.gitsync.seal.", StringComparison.Ordinal),
                "an import that runs exactly as asked has nothing about a seal to say");
    }

    /// <summary>Runs one person-initiated operation under the signed-in user and returns the
    /// activity's terminal log.</summary>
    private async Task<ActivityLog> Run(IObservable<string> operation, CancellationToken cancellationToken)
    {
        Task<string> started;
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name }))
            started = operation.Timeout(TestTimeouts.CrossSilo).Await(cancellationToken);
        var activityPath = await started;
        Output.WriteLine($"activity: {activityPath}");

        var log = await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && l.Status != ActivityStatus.Running)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        foreach (var line in log!.Messages)
            Output.WriteLine($"  [{line.LogLevel}] {line.MessageKey ?? "-"}: {line.Message}");
        return log;
    }

    /// <summary>A Space with a sync config for this repository and a credential for the user and for
    /// whoever the config write attributed itself to.</summary>
    private async Task<string> ArmedSpace(string prefix, CancellationToken cancellationToken)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Person-import space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        foreach (var owner in new[] { configNode.CreatedBy, UserId }
                     .Where(o => !string.IsNullOrEmpty(o)).Distinct())
            await Credentials
                .Save(owner!, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        return space;
    }

    private async Task<GitHubSyncConfig> ConfigWhen(
        string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

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

    /// <summary>The GitHub transport, reduced to the one question this test asks: which ref did the
    /// import request? Everything else throws.</summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private ImmutableList<string> requested = ImmutableList<string>.Empty;

        /// <summary>Every commitish a fetch has asked for, in order.</summary>
        public ImmutableList<string> Requested => requested;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            ImmutableInterlocked.Update(ref requested, list => list.Add(commitish));
            // An empty snapshot at the requested ref: the RECORD of what was asked is the measurement.
            return Observable.Return(new RepoSnapshot(commitish, Array.Empty<RepoFile>()));
        }

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => NotUsed<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
            => NotUsed<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
            => NotUsed<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException("APersonsImportLandsOnTheSealTest's repo client answers only Fetch."));
    }
}
