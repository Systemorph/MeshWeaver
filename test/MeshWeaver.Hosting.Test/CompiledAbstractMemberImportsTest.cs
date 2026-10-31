#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>Content of a runtime-compiled NodeType whose member is declared as an ABSTRACT base —
/// the measured shape of #6639 (a section whose <c>cover</c> is a <c>SectionCover</c>). Loaded by
/// the test from a COLLECTIBLE copy of this assembly, which is what a NodeType compile produces.</summary>
public record CompiledSectionContent(string Name, CompiledSectionCover Cover);

/// <summary>The abstract member type.</summary>
public abstract record CompiledSectionCover(string Currency);

/// <summary>The subtype the repository file names in <c>$type</c>.</summary>
public record CompiledNonProportionalCover(string Currency, decimal Retention, decimal Limit)
    : CompiledSectionCover(Currency);

/// <summary>A second subtype of the same base.</summary>
public record CompiledProportionalCover(string Currency, decimal Share)
    : CompiledSectionCover(Currency);

/// <summary>Content whose abstract-typed member is a READ-ONLY COLLECTION — the shape the mesh's
/// <c>ReadOnlyCollectionConverter</c> reads element by element.</summary>
public record CompiledLayeredContent(string Name, IReadOnlyCollection<CompiledSectionCover> Layers);

/// <summary>
/// 🚨 <b>A GitSync import must not refuse content whose member is declared as an abstract base of a
/// runtime-compiled NodeType (#6639).</b> Measured on the public instance: a forced update of a
/// Space refused the WHOLE snapshot because fourteen section files gave <i>"Deserialization of
/// interface or abstract types is not supported. Type 'SectionCover'. Path: $.cover"</i>, while the
/// NodeType defining those types was compiled <c>Ok</c>.
///
/// <para>The content type resolves on the importing hub through the mesh content-type map, but its
/// abstract member's <c>$type</c> does not: a compiled build's subtypes are by design never adopted
/// into a long-lived hub's type registry. That is a type this process cannot INSTANTIATE here, not a
/// field it would DROP — so the content is kept as its raw JSON and imported verbatim, <c>$type</c>
/// and all. Strict import still refuses real field loss (<see cref="RefusedSourceSaysItIsRefusedTest"/>).</para>
///
/// <para>The types are loaded from a collectible copy of this assembly and registered on the mesh
/// content-type map only — exactly the state a compiled NodeType leaves a mesh hub in.</para>
/// </summary>
public class CompiledAbstractMemberImportsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string HeadSha = "6639abcd0000000000000000000000000000abcd";

    private readonly SnapshotRepoClient repoClient = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <summary>The measured shape: section files whose <c>cover</c> names a compiled subtype import,
    /// and each stored node keeps its content with every <c>$type</c> and member.</summary>
    [Fact(Timeout = 120_000)]
    public async Task SectionsWithACompiledAbstractMember_ImportVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, typeof(CompiledSectionContent), out var compiledContent);
        try
        {
            compiledContent.Assembly.IsCollectible.Should().BeTrue(
                "the defect is specific to a compiled build's types, which no long-lived hub registers");
            var space = await ArmedSpace(ct);
            repoClient.Files =
            [
                new RepoFile("Submissions/MotorSection.json", $$$$"""
                    {"id":"MotorSection","nodeType":"{{{{compiledNodeType}}}}","content":{"$type":"{{{{nameof(CompiledSectionContent)}}}}","name":"Motor","cover":{"$type":"{{{{nameof(CompiledNonProportionalCover)}}}}","currency":"EUR","retention":1000000,"limit":5000000}}}
                    """),
                new RepoFile("Submissions/PropertySection.json", $$$$"""
                    {"id":"PropertySection","nodeType":"{{{{compiledNodeType}}}}","content":{"$type":"{{{{nameof(CompiledSectionContent)}}}}","name":"Property","cover":{"$type":"{{{{nameof(CompiledProportionalCover)}}}}","currency":"CHF","share":0.25}}}
                    """),
            ];

            var outcome = await Sync.ReimportAtCommit(space, HeadSha, UserId)
                .Materialize()
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            outcome.Exception.Should().BeNull(
                "a subtype this hub cannot instantiate is not a field it would drop; the import must "
                + "store the content, not refuse the snapshot (#6639): " + outcome.Exception?.Message);

            var motor = await ContentOf($"{space}/Submissions/MotorSection", ct);
            motor.GetProperty("$type").GetString().Should().Be(nameof(CompiledSectionContent));
            var cover = motor.GetProperty("cover");
            cover.GetProperty("$type").GetString().Should().Be(nameof(CompiledNonProportionalCover));
            cover.GetProperty("retention").GetDecimal().Should().Be(1_000_000m,
                "the subtype's own members are stored, not cut to the abstract base");
            cover.GetProperty("limit").GetDecimal().Should().Be(5_000_000m);

            var property = await ContentOf($"{space}/Submissions/PropertySection", ct);
            property.GetProperty("cover").GetProperty("$type").GetString()
                .Should().Be(nameof(CompiledProportionalCover));
            property.GetProperty("cover").GetProperty("share").GetDecimal().Should().Be(0.25m);

            await CommitAcknowledged(space, ct);
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>Negative control: a <c>$type</c> no build in this process defines, under the same
    /// abstract member. The node is stored with its authored JSON verbatim instead of refusing the
    /// snapshot.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnknownSubtypeUnderAnAbstractMember_ImportsVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, typeof(CompiledSectionContent), out _);
        try
        {
            var space = await ArmedSpace(ct);
            repoClient.Files =
            [
                new RepoFile("Submissions/PropertySection.json", $$$$"""
                    {"id":"PropertySection","nodeType":"{{{{compiledNodeType}}}}","content":{"$type":"{{{{nameof(CompiledSectionContent)}}}}","name":"Property","cover":{"$type":"NoSuchCoverAnywhere","currency":"CHF","attachment":42}}}
                    """),
            ];

            var outcome = await Sync.ReimportAtCommit(space, HeadSha, UserId)
                .Materialize()
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            outcome.Exception.Should().BeNull(
                "an unresolvable subtype is not lossy when the raw JSON is kept, so it must not "
                + "refuse the snapshot: " + outcome.Exception?.Message);

            var cover = (await ContentOf($"{space}/Submissions/PropertySection", ct)).GetProperty("cover");
            cover.GetProperty("$type").GetString().Should().Be("NoSuchCoverAnywhere");
            cover.GetProperty("attachment").GetInt32().Should().Be(42,
                "the authored bytes are stored verbatim");

            await CommitAcknowledged(space, ct);
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>The collection shape: an <c>IReadOnlyCollection</c> of the abstract base. The
    /// collection converter reads elements one by one and must report an uninstantiable element as
    /// what it is (a <see cref="NotSupportedException"/>), so the strict import keeps the raw JSON
    /// instead of refusing the snapshot. It re-threw that as an <see cref="InvalidOperationException"/>,
    /// which the strict import treats as a lossy bind and refuses.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAbstractElementInACollection_ImportsVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, typeof(CompiledLayeredContent), out _);
        try
        {
            var space = await ArmedSpace(ct);
            repoClient.Files =
            [
                new RepoFile("Submissions/LayeredSection.json", $$$$"""
                    {"id":"LayeredSection","nodeType":"{{{{compiledNodeType}}}}","content":{"$type":"{{{{nameof(CompiledLayeredContent)}}}}","name":"Layered","layers":[{"$type":"{{{{nameof(CompiledNonProportionalCover)}}}}","currency":"EUR","retention":1,"limit":2},{"$type":"NoSuchLayerAnywhere","currency":"CHF","attachment":7}]}}
                    """),
            ];

            var outcome = await Sync.ReimportAtCommit(space, HeadSha, UserId)
                .Materialize()
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            outcome.Exception.Should().BeNull(
                "an element this hub cannot instantiate is not lossy when the raw JSON is kept — "
                + "the collection shape must not refuse the snapshot either: " + outcome.Exception?.Message);

            var layers = (await ContentOf($"{space}/Submissions/LayeredSection", ct)).GetProperty("layers");
            layers.GetArrayLength().Should().Be(2, "both authored elements are stored");
            layers[0].GetProperty("$type").GetString().Should().Be(nameof(CompiledNonProportionalCover));
            layers[0].GetProperty("retention").GetDecimal().Should().Be(1m);
            layers[1].GetProperty("$type").GetString().Should().Be("NoSuchLayerAnywhere");
            layers[1].GetProperty("attachment").GetInt32().Should().Be(7, "the authored bytes are stored verbatim");

            await CommitAcknowledged(space, ct);
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>Loads this test assembly's bytes into a collectible context and registers the
    /// copy of <paramref name="contentType"/> on the mesh content-type map — what
    /// <c>MeshNodeHubFactory</c> does for a compiled NodeType. The mesh hub's own type registry is
    /// left untouched.</summary>
    private AssemblyLoadContext LoadCompiledCopy(string nodeTypePath, Type contentType, out Type compiledContent)
    {
        var file = contentType.Assembly.Location;
        file.Should().NotBeNullOrEmpty("the collectible copy is loaded from this assembly's file");
        var alc = new AssemblyLoadContext("compiled-abstract-member-" + Guid.NewGuid().ToString("N")[..8],
            isCollectible: true);
        Assembly copy;
        using (var stream = File.OpenRead(file))
            copy = alc.LoadFromStream(stream);
        compiledContent = copy.GetType(contentType.FullName!, throwOnError: true)!;
        compiledContent.Should().NotBeSameAs(contentType);
        Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>()
            .Register(compiledContent, nodeTypePath);
        return alc;
    }

    /// <summary>The NodeType the section nodes declare, in a Space of its own so the import's
    /// prune never reaches it. It carries no source: its content type is the collectible copy the
    /// test registers, standing in for the build a compile would have produced.</summary>
    private async Task<string> DeclaredNodeType(CancellationToken ct)
    {
        var typesSpace = "Types" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(typesSpace)
        {
            NodeType = "Space",
            Name = "Compiled types",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        await NodeFactory.CreateNode(new MeshNode("CompiledSection", typesSpace)
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Compiled section",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        return $"{typesSpace}/CompiledSection";
    }

    /// <summary>The stored node's content as JSON, read from the authoritative node stream (the
    /// import has completed, so the node exists).</summary>
    private async Task<JsonElement> ContentOf(string path, CancellationToken ct)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        var json = JsonSerializer.Serialize(node!.Content, Mesh.JsonSerializerOptions);
        Output.WriteLine($"{path}: {json}");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task CommitAcknowledged(string space, CancellationToken ct)
    {
        var config = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n?.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is
                { LastSyncCommitSha: HeadSha })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        config.Should().NotBeNull("the imported commit is acknowledged");
    }

    private async Task<string> ArmedSpace(CancellationToken ct)
    {
        var space = "Compiled" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Compiled-abstract-member space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return space;
    }

    /// <summary>The transport reduced to Fetch: it serves the files the test sets.</summary>
    private sealed class SnapshotRepoClient : IGitHubRepoClient
    {
        public IReadOnlyList<RepoFile> Files { get; set; } = Array.Empty<RepoFile>();

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(new RepoSnapshot(commitish, Files));

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
            string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubPullRequestDetail>();
        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();
        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException("this test's transport answers only Fetch"));
    }
}
