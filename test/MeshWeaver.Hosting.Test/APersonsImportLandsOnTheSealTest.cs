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
/// 🚨 <b>A person's import of a repository whose bundles this instance runs lands on the SEALED
/// commit — never the branch tip.</b> MeshWeaver#3845 hole 3.
///
/// <para><b>The hole.</b> The unattended lanes — the green-build webhook, the seal's arrival, the
/// first import, the boot install — all ask <see cref="SealedSyncGate"/> which commit a repository's
/// sources may land on. The two GUI paths did not: <c>GitHubActionArea</c>'s <b>Update from GitHub</b>
/// called <see cref="GitHubActivityExtensions.UpdateToLatestFromGitHub"/>, which resolved the branch
/// at fetch time, and the settings tab's <b>Re-import at this commit</b> passed whatever was typed —
/// its placeholder reads "commit SHA or branch" — to
/// <see cref="GitHubActivityExtensions.ReimportFromGitHub"/>. <c>SyncRefContract</c> stated that as
/// the rule ("only a human-initiated Update may read a branch tip"), and the maintainer reversed it on
/// 2026-09-17: sources on a tree no bundle for this identity was baked from are declined on their
/// fingerprint whoever pressed the button.</para>
///
/// <para><b>What is measured, and what could falsify it.</b> The ref that reaches
/// <see cref="IGitHubRepoClient.Fetch"/> — not a log line and not a decision function, because the
/// defect is precisely what the fetch receives. Against the pre-fix code the first two facts fetch
/// <c>main</c> and the third fetches <c>main</c> where it must fetch nothing. The fourth is the control
/// that keeps the other three honest: a repository this instance runs NO publication of must still
/// read the branch (hole 1's adjudication), or the gate would be a switch that blocks every import.</para>
///
/// <para>One seam is substituted, the GitHub transport, exactly as in
/// <c>HeldSourceSaysItIsHeldTest</c>. The published bundle root is a REAL directory carrying the
/// markers <c>publish-bake-bundles.sh</c> writes, read by the real index on the real FileSystem pool.
/// Everything between is the real mesh: the real activity runner, the real gate, the real sync
/// service, the real node streams.</para>
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
    public async Task UpdateToLatest_OfASealedRepository_FetchesTheSealedCommit_NeverTheBranch()
    {
        StagePublication(RepoFullName, sealedState: true);
        var space = await ArmedSpace("Update", TestContext.Current.CancellationToken);

        var log = await Run(Mesh.UpdateToLatestFromGitHub(space, UserId), TestContext.Current.CancellationToken);

        // The activity is terminal and the import runs INSIDE it, so every fetch it made is already
        // recorded: this list is complete, not a window.
        repoClient.Requested.Should().Equal([SealedSha],
            "a person's Update of a repository whose bundles this instance runs lands on the commit those "
            + "bundles were baked from — never on the branch, which would put sources ahead of the bytes");
        log.Status.Should().Be(ActivityStatus.Warning,
            "the person asked for latest and got the sealed commit — a quiet Succeeded would say they got "
            + "what they asked for");
        log.Messages.Select(m => m.MessageKey).Should().Contain("activity.gitsync.seal.landsOnSeal");
        log.Messages.Select(m => m.MessageKey).Should().Contain("activity.gitsync.seal.advanceBySeal",
            "a redirect must name what moves the Space further — roll the instance, or the publishing lane");

        var config = await ConfigWhen(space,
            c => string.Equals(c.LastSyncCommitSha, SealedSha, StringComparison.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken);
        config.LastSyncCommitSha.Should().Be(SealedSha);
    }

    [Fact(Timeout = 120_000)]
    public async Task ReimportAtATypedBranch_OfASealedRepository_FetchesTheSealedCommit()
    {
        StagePublication(RepoFullName, sealedState: true);
        var space = await ArmedSpace("Reimport", TestContext.Current.CancellationToken);

        var log = await Run(Mesh.ReimportFromGitHub(space, "main", UserId), TestContext.Current.CancellationToken);

        repoClient.Requested.Should().Equal([SealedSha],
            "the re-import field accepts a branch, and a branch typed there was a tip import with a text box "
            + "in front of it");
        log.Messages.Select(m => m.MessageKey).Should().Contain("activity.gitsync.seal.landsOnSeal");
    }

    [Fact(Timeout = 120_000)]
    public async Task UpdateToLatest_AgainstATornPublication_FetchesNothing_AndSaysWhy()
    {
        StagePublication(RepoFullName, sealedState: false);
        var space = await ArmedSpace("Torn", TestContext.Current.CancellationToken);

        var log = await Run(Mesh.UpdateToLatestFromGitHub(space, UserId), TestContext.Current.CancellationToken);

        repoClient.Requested.Should().BeEmpty(
            "'we could not establish a commit' and 'the branch tip' are different answers — a torn seal "
            + "imports nothing rather than falling back to what was asked");
        log.Status.Should().Be(ActivityStatus.Warning,
            "a hold is neither a success nor a failure a retry could change — the seal landing changes it");
        log.Messages.Select(m => m.MessageKey).Should().Contain("activity.gitsync.seal.heldNotSealed",
            "the person must be told WHICH publication holds the Space and why, in their own language");
        log.Messages.Single(m => m.MessageKey == "activity.gitsync.seal.heldNotSealed").Message
            .Should().Contain("no completion sentinel");
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
