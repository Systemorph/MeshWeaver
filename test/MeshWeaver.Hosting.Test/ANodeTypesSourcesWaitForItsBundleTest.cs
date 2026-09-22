using System;
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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The acceptance criterion of MeshWeaver#3845, measured on a real mesh:</b> an ADOPTED
/// NodeType's <c>CurrentSourceFingerprint</c> does not move until a bundle for this instance's
/// framework identity records the fingerprint the repository's sources would produce — hole 4,
/// adopt-then-sync per NodeType.
///
/// <para><b>What the tree-level gate cannot do.</b> <see cref="SealedSyncGate"/> lands the Space on
/// the commit this instance's bundles were baked from, which is a REPOSITORY fact. Adoption is
/// decided per TYPE, and a publication can be sealed, at the right commit, under the right identity,
/// and still not carry the bundle a given type needs (#3461 measured two producers composing
/// different module sets for one identity). So the tree can be right while one type's sources move
/// onto bytes this instance does not have — which is exactly the <c>StaleAdopted</c> window #3583
/// measured, one type at a time.</para>
///
/// <para><b>What is measured, and what falsifies it.</b> A type adopted at commit A, then an import
/// of commit B whose sources no bundle carries: its source node keeps A's TEXT, its record keeps A's
/// fingerprint and <see cref="BuildProvenance.AdoptedVerified"/>, the Space's OTHER node takes B
/// (the hold is per type, not per Space), and the config keeps commit A while naming the held type
/// and the fingerprint it waits for. Against the pre-#3845-hole-4 code all four of those fail: the
/// source text becomes B's, the fingerprint moves, and the config advances to B. Then the release:
/// a bundle recording the wanted fingerprint lands, the same import runs again, and the sources
/// arrive.</para>
///
/// <para>One seam is substituted, the GitHub transport. The published bundle root is a REAL directory
/// with real bundle archives (<see cref="BundleWriter"/>) under the markers
/// <c>publish-bake-bundles.sh</c> writes, and the adoption is a real
/// <see cref="PrebuiltAssemblySeeder"/> seed of real PE bytes — so the record this gate reads is the
/// one the production adoption path writes.</para>
/// </summary>
public class ANodeTypesSourcesWaitForItsBundleTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/bundle-held";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string SealedSourceName = "plugins";

    /// <summary>The commit the bundle on the shelf was baked from.</summary>
    private const string CommitA = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";

    /// <summary>A later commit whose sources no bundle carries — yet.</summary>
    private const string CommitB = "bb11bb22bb33bb44bb55bb66bb77bb88bb99bb00";

    private readonly RecordingRepoClient repoClient = new();

    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-bundle-held-" + Guid.NewGuid().ToString("N")[..12]);

    private static string UserId => TestUsers.Admin.ObjectId!;

    private string Space = "";
    private string TypePath => $"{Space}/Widget";
    private string SourcePath => $"{TypePath}/Source/WidgetView";
    private string NotesPath => $"{Space}/Notes";

    private string PublicationDirectory => Path.Combine(
        publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, SealedSourceName);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                // Layer the published-bundle root onto the host's configuration, never replace it.
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
                "The IConfiguration registration is neither a factory nor an instance — update this "
                + "hook rather than replacing the host's configuration.");

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    [Fact(Timeout = 300_000)]
    public async Task AnAdoptedTypesSources_WaitForABundleThatCarriesThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Space = "Held" + Guid.NewGuid().ToString("N")[..8];
        StageSeal(CommitA);
        repoClient.Stage(CommitA, Tree("class WidgetView { }", "Notes at A"));
        repoClient.Stage(CommitB, Tree("class WidgetView { int n; }", "Notes at B"));

        // ── 1. The Space takes commit A, and its type ADOPTS a bundle built from those sources ──
        await Armed(cancellationToken);
        await Sync.ReimportAtCommit(Space, CommitA, UserId)
            .Timeout(TestTimeouts.CrossSilo).Await(cancellationToken);

        // Reimport requests the initial release without awaiting its compile. A source fingerprint
        // only says the inputs arrived: seeding at that point races the still-running compile's
        // terminal stamp, which can replace AdoptedVerified with Compiled after adoption. Join the
        // release this fixture initiated before establishing the adopted-type premise below.
        var initialBuild = await DefinitionWhen(d =>
                d.CurrentSourceFingerprint is { Length: > 0 }
                && d.CompilationStatus is CompilationStatus.Ok
                && d.BuildProvenance is BuildProvenance.Compiled
                && !d.IsDirty
                && d.RequestedReleaseAt is { } requested
                && d.LastReleaseRequestHandledAt >= requested,
            cancellationToken);
        var adoptedFingerprint = initialBuild.CurrentSourceFingerprint!;
        Output.WriteLine($"live fingerprint at {CommitA[..8]}: {adoptedFingerprint}");
        StageBundle("widget-a.zip", adoptedFingerprint);

        var seeded = await PrebuiltAssemblySeeder.SeedDetailed(
                Mesh, TypePath, TestAssemblyBytes(), pdbBytes: null,
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
                logger: null, dependencies: null, sourceFingerprint: adoptedFingerprint)
            .Should().Within(TestTimeouts.WriteConvergence).Emit("a seed completes either way",
                cancellationToken);
        Output.WriteLine($"seed outcome: {seeded}");
        var adopted = await DefinitionWhen(d => d.BuildProvenance is BuildProvenance.AdoptedVerified,
            cancellationToken);
        adopted.CurrentSourceFingerprint.Should().Be(adoptedFingerprint,
            "the premise: the type is adopted AND verified against the sources the mesh holds");

        // ── 2. Commit B moves the type's sources, and NO bundle carries the fingerprint they make ──
        await Sync.ReimportAtCommit(Space, CommitB, UserId)
            .Timeout(TestTimeouts.CrossSilo).Await(cancellationToken);

        // 🚨 Read the config with a FALLBACK to whatever it currently says, never a bare wait:
        // against the pre-hole-4 code the predicate is never satisfied, and a TimeoutException names
        // no field — the assertion below must be what fails, and it must say what the node said.
        var held = await ConfigWhenOrCurrent(
            c => c.BundleHeldNodeTypes is { IsEmpty: false }, cancellationToken);
        Output.WriteLine(
            $"after the held import: commit={held.LastSyncCommitSha ?? "(none)"} "
            + $"outcome={held.LastSyncOutcome} held={held.BundleHeldNodeTypes?.Count ?? 0}");
        held.BundleHeldNodeTypes.Should().NotBeNull(
            "an import that held a type's sources records WHICH types and what each waits for — a "
            + "hold that lives only in a log line is indistinguishable from a source that is up to "
            + "date (#4063, one level down)");
        var wanted = held.BundleHeldNodeTypes!.Single();
        Output.WriteLine($"held: {wanted.Path} has {wanted.HeldFingerprint}, waits for {wanted.WantedFingerprint}");
        wanted.Path.Should().Be(TypePath);
        wanted.HeldFingerprint.Should().Be(adoptedFingerprint);
        wanted.WantedFingerprint.Should().NotBe(adoptedFingerprint,
            "the incoming sources are different text, so they fold to a different fingerprint");
        held.LastSyncCommitSha.Should().Be(CommitA,
            "a partially-held Space does not hold commit B — the baseline stays on the commit whose "
            + "content the mesh genuinely has, so the held files are still in the next diff");

        (await SourceText(cancellationToken)).Should().Contain("class WidgetView { }")
            .And.NotContain("int n",
                "the held type keeps the sources its adopted bytes were compiled from — this is the "
                + "whole acceptance criterion of #3845");
        var stillAdopted = await Definition(cancellationToken);
        stillAdopted.CurrentSourceFingerprint.Should().Be(adoptedFingerprint,
            "CurrentSourceFingerprint does not move until a matching bundle exists");
        stillAdopted.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            "the type goes AdoptedVerified → AdoptedVerified, never through StaleAdopted");

        // …and the hold is PER TYPE: the rest of the Space took commit B.
        (await NotesNameWhen("Notes at B", cancellationToken)).Should().Be("Notes at B",
            "a hold on one NodeType must not freeze the Space — hole 4 is a selective import, not a "
            + "stopped one");

        // ── 3. The release: a bundle recording the wanted fingerprint lands ───────────────────
        StageBundle("widget-b.zip", wanted.WantedFingerprint);
        await Sync.ReimportAtCommit(Space, CommitB, UserId)
            .Timeout(TestTimeouts.CrossSilo).Await(cancellationToken);

        (await SourceTextWhen(text => text.Contains("int n"), cancellationToken))
            .Should().Contain("int n",
                "the bundle that was waited for is on the shelf, so the sources land");
        var released = await ConfigWhen(
            c => string.Equals(c.LastSyncCommitSha, CommitB, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        released.BundleHeldNodeTypes.Should().BeNull(
            "the record describes the LAST attempt: a released hold leaves no entry behind");
    }

    // ── staging ───────────────────────────────────────────────────────────────

    /// <summary>The repository tree at one commit: the NodeType node, its source, and one node that
    /// is not compile input at all (the "rest of the Space", whose name carries the commit).</summary>
    private static IReadOnlyList<RepoFile> Tree(string code, string notes) =>
    [
        new("Widget.json",
            """
            {"$type":"MeshNode","id":"Widget","name":"Widget","nodeType":"NodeType","state":"Active",
             "content":{"$type":"NodeTypeDefinition","description":"held-type fixture"}}
            """),
        new("Widget/Source/WidgetView.cs",
            $"""
            // <meshweaver>
            // Id: WidgetView
            // DisplayName: Widget View
            // </meshweaver>

            {code}
            """),
        new("Notes.json",
            $$"""
            {"$type":"MeshNode","id":"Notes","name":"{{notes}}","nodeType":"Markdown","state":"Active"}
            """),
    ];

    /// <summary>The seal's three markers, at <paramref name="commit"/>. The sentinel is rewritten by
    /// <see cref="StageBundle"/> as bundles are added, so it always lists what is on disk.</summary>
    private void StageSeal(string commit)
    {
        Directory.CreateDirectory(PublicationDirectory);
        File.WriteAllText(
            Path.Combine(PublicationDirectory, SealedPublicationIndex.RepositoryMarkerFileName), RepoFullName);
        File.WriteAllText(
            Path.Combine(PublicationDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), commit);
        File.WriteAllText(
            Path.Combine(PublicationDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Empty);
    }

    /// <summary>Writes a REAL bundle archive recording <paramref name="fingerprint"/> for the type,
    /// and re-seals the publication listing every archive present — the shape the inventory reads.</summary>
    private void StageBundle(string fileName, string fingerprint)
    {
        var path = Path.Combine(PublicationDirectory, fileName);
        using (var file = File.Create(path))
            BundleWriter.Write(
                file, plugin: "Widget", version: "1.0.0",
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
                assemblies:
                [
                    new BundleWriter.AssemblyEntry(TypePath, () => new MemoryStream(TestAssemblyBytes()))
                    {
                        SourceFingerprint = fingerprint,
                    },
                ]);
        var archives = Directory.EnumerateFiles(PublicationDirectory, "*.zip")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal);
        File.WriteAllLines(
            Path.Combine(PublicationDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName), archives!);
    }

    /// <summary>Real PE bytes with a real MVID — the test assembly, as the adoption tests use.</summary>
    private static byte[] TestAssemblyBytes() =>
        File.ReadAllBytes(typeof(ANodeTypesSourcesWaitForItsBundleTest).Assembly.Location);

    private async Task Armed(CancellationToken cancellationToken)
    {
        await NodeFactory.CreateNode(new MeshNode(Space)
        {
            NodeType = "Space",
            Name = "Bundle-held space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        var configNode = await Sync
            .SaveConfig(Space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        foreach (var owner in new[] { configNode.CreatedBy, UserId }
                     .Where(o => !string.IsNullOrEmpty(o)).Distinct())
            await Credentials
                .Save(owner!, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
    }

    // ── reads ─────────────────────────────────────────────────────────────────

    private IObservable<NodeTypeDefinition> Definitions =>
        Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null)
            .Select(d => d!);

    private async Task<NodeTypeDefinition> DefinitionWhen(
        Func<NodeTypeDefinition, bool> predicate, CancellationToken cancellationToken)
        => await Definitions.Where(predicate).FirstAsync()
            .Timeout(TestTimeouts.CrossSilo).Await(cancellationToken);

    private async Task<NodeTypeDefinition> Definition(CancellationToken cancellationToken)
        => await Definitions.FirstAsync().Timeout(TestTimeouts.Convergence).Await(cancellationToken);

    /// <summary>The config once it satisfies <paramref name="predicate"/>, or — when it never does
    /// within the bound — whatever it says RIGHT NOW, so the caller's assertion is what fails and
    /// names the field.</summary>
    private async Task<GitHubSyncConfig> ConfigWhenOrCurrent(
        Func<GitHubSyncConfig, bool> predicate, CancellationToken cancellationToken)
    {
        var configs = Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(Space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is not null)
            .Select(n => n!.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!);
        return await configs.Where(predicate).FirstAsync()
            .Timeout(TestTimeouts.Convergence, configs.FirstAsync())
            .Timeout(TestTimeouts.CrossSilo)
            .Await(cancellationToken);
    }

    private async Task<GitHubSyncConfig> ConfigWhen(
        Func<GitHubSyncConfig, bool> predicate, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(Space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(cancellationToken);
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

    private async Task<string> SourceText(CancellationToken cancellationToken)
        => await SourceTextWhen(_ => true, cancellationToken);

    private async Task<string> SourceTextWhen(
        Func<string, bool> predicate, CancellationToken cancellationToken)
        => await Mesh.GetWorkspace().GetMeshNodeStream(SourcePath)
            .Select(n => n?.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)?.Code)
            .Where(code => code is { Length: > 0 } && predicate(code))
            .Select(code => code!)
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(cancellationToken);

    /// <summary>The name of the Space's non-compile-input node, once it reads
    /// <paramref name="expected"/> — the assertion is that the rest of the Space DID advance while
    /// one type was held, and it WAITS for that rather than reading the first emission a stream cache
    /// can replay from before the write.</summary>
    private async Task<string> NotesNameWhen(string expected, CancellationToken cancellationToken)
        => await Mesh.GetWorkspace().GetMeshNodeStream(NotesPath)
            .Select(n => n?.Name)
            .Where(name => string.Equals(name, expected, StringComparison.Ordinal))
            .Select(name => name!)
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(cancellationToken);

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

    /// <summary>The GitHub transport, serving a staged tree per commit and recording what was asked
    /// for. Everything else throws, so a future caller is told rather than served a fake answer.</summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private ImmutableDictionary<string, IReadOnlyList<RepoFile>> trees =
            ImmutableDictionary<string, IReadOnlyList<RepoFile>>.Empty;
        private ImmutableList<string> requested = ImmutableList<string>.Empty;

        /// <summary>Every commitish a fetch asked for, in order.</summary>
        public ImmutableList<string> Requested => requested;

        public void Stage(string commit, IReadOnlyList<RepoFile> files)
            => ImmutableInterlocked.Update(ref trees, map => map.SetItem(commit, files));

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            ImmutableInterlocked.Update(ref requested, list => list.Add(commitish));
            return trees.TryGetValue(commitish, out var files)
                ? Observable.Return(new RepoSnapshot(commitish, [.. files]))
                : Observable.Throw<RepoSnapshot>(new NotSupportedException(
                    $"ANodeTypesSourcesWaitForItsBundleTest staged no tree for '{commitish}'."));
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
            new NotSupportedException(
                "ANodeTypesSourcesWaitForItsBundleTest's repo client answers only Fetch."));
    }
}
