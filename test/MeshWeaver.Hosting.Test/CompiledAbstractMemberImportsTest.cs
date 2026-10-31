#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>A second subtype, so the resolver has a real choice to make.</summary>
public record CompiledProportionalCover(string Currency, decimal Share)
    : CompiledSectionCover(Currency);

/// <summary>Content whose abstract member has two subtypes sharing ONE short name — the contested
/// discriminator a compiled build can carry when two namespaces (here: two declaring classes) each
/// define a record of the same name.</summary>
public record CompiledContestedContent(string Name, CompiledContestedItem Item);

/// <summary>The abstract member type of <see cref="CompiledContestedContent"/>.</summary>
public abstract record CompiledContestedItem(string Code);

/// <summary>A subtype whose short name nothing else in the build uses — the control.</summary>
public record CompiledUniqueItem(string Code, int Rank) : CompiledContestedItem(Code);

/// <summary>Declares one of the two same-named subtypes.</summary>
public static class FirstContestedFamily
{
    /// <summary>Same short name as <see cref="SecondContestedFamily.CompiledSharedItem"/>.</summary>
    public record CompiledSharedItem(string Code, decimal Amount) : CompiledContestedItem(Code);
}

/// <summary>Declares the other same-named subtype.</summary>
public static class SecondContestedFamily
{
    /// <summary>Same short name as <see cref="FirstContestedFamily.CompiledSharedItem"/>.</summary>
    public record CompiledSharedItem(string Code, string Label) : CompiledContestedItem(Code);
}

/// <summary>
/// 🚨 <b>A GitSync import must read content whose member is declared as an abstract base of a
/// runtime-compiled NodeType (#6639).</b> Measured on the public instance: a forced update of a
/// Space refused the WHOLE snapshot because fourteen section files gave <i>"Deserialization of
/// interface or abstract types is not supported. Type 'SectionCover'. Path: $.cover"</i>, while the
/// NodeType defining those types was compiled <c>Ok</c>.
///
/// <para>The content type itself resolved — through the mesh content-type map, which is how every
/// hub other than the NodeType's own reaches a compiled type. Its abstract member did not: the
/// polymorphic resolver found subtypes only in the reading hub's type registry, and a compiled
/// build's types are by design never adopted there. So the binding failed, and under the import's
/// strict options the failure refused the snapshot.</para>
///
/// <para>The types are loaded from a collectible copy of this assembly and registered on the mesh
/// content-type map only — exactly the state a compiled NodeType leaves a mesh hub in. Nothing is
/// registered on the mesh hub's own type registry.</para>
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

    [Fact(Timeout = 120_000)]
    public async Task AnAbstractMemberWithACompiledSubtype_Imports()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, out var compiledContent);
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
            ];

            var outcome = await Sync.ReimportAtCommit(space, HeadSha, UserId)
                .Materialize()
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            outcome.Exception.Should().BeNull(
                "the content type resolves mesh-wide and the subtype is in the same compiled build; "
                + "the import must bind it, not refuse the snapshot (#6639): "
                + outcome.Exception?.Message);

            var cover = await CoverOf($"{space}/Submissions/MotorSection", ct);
            cover.GetProperty("$type").GetString().Should().Be(nameof(CompiledNonProportionalCover));
            cover.GetProperty("retention").GetDecimal().Should().Be(1_000_000m,
                "the subtype's own members must survive the import, not be cut to the abstract base");
            cover.GetProperty("limit").GetDecimal().Should().Be(5_000_000m);

            await CommitAcknowledged(space, ct);
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>The resolver half on its own: the mesh hub's options BIND the abstract member to the
    /// compiled subtype — typed, not merely preserved as raw JSON — and write it back with the
    /// subtype's discriminator and members. Nothing is registered on the hub's type registry.</summary>
    [Fact(Timeout = 60_000)]
    public async Task TheMeshOptionsBindACompiledSubtype_AndWriteItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, out var compiledContent);
        try
        {
            var json = $$$"""
                {"name":"Motor","cover":{"$type":"{{{nameof(CompiledNonProportionalCover)}}}","currency":"EUR","retention":1000000,"limit":5000000}}
                """;
            var typed = JsonSerializer.Deserialize(json, compiledContent, Mesh.JsonSerializerOptions);
            var cover = compiledContent.GetProperty(nameof(CompiledSectionContent.Cover))!.GetValue(typed);
            cover.Should().NotBeNull();
            cover!.GetType().Name.Should().Be(nameof(CompiledNonProportionalCover),
                "the subtype is in the same compiled build as its abstract base");
            cover.GetType().Assembly.Should().BeSameAs(compiledContent.Assembly,
                "the subtype must come from the build being read, never the default context's copy");

            using var written = JsonDocument.Parse(
                JsonSerializer.Serialize(typed, compiledContent, Mesh.JsonSerializerOptions));
            var writtenCover = written.RootElement.GetProperty("cover");
            writtenCover.GetProperty("$type").GetString().Should().Be(nameof(CompiledNonProportionalCover));
            writtenCover.GetProperty("retention").GetDecimal().Should().Be(1_000_000m,
                "a write through the abstract member must carry the subtype's own members");
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>Negative control: a <c>$type</c> no build in this process defines, under the same
    /// abstract member. Nothing can type it, and the import does not need to — the node is stored
    /// with its authored JSON verbatim instead of refusing the snapshot.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnknownSubtypeUnderAnAbstractMember_ImportsVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, out _);
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

            var cover = await CoverOf($"{space}/Submissions/PropertySection", ct);
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

    /// <summary>A short name two subtypes of the same build share resolves to NEITHER: content
    /// naming it stays raw JSON, never bound to whichever subtype the assembly enumerated first
    /// (which would drop the other's members and materialise foreign defaults). A subtype whose
    /// name is unique in the same build still binds — the contest costs only the contested name.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AContestedSubtypeName_StaysRaw_AndAUniqueSiblingStillBinds()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiledNodeType = await DeclaredNodeType(ct);
        var alc = LoadCompiledCopy(compiledNodeType, out var compiledSection);
        try
        {
            var contested = compiledSection.Assembly
                .GetType(typeof(CompiledContestedContent).FullName!, throwOnError: true)!;
            Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>()
                .Register(contested, compiledNodeType + "Contested");

            var contestedJson = $$$"""
                {"$type":"{{{nameof(CompiledContestedContent)}}}","name":"A","item":{"$type":"CompiledSharedItem","code":"X","label":"second family"}}
                """;
            var read = JsonSerializer.Deserialize<object>(contestedJson, Mesh.JsonSerializerOptions);
            Output.WriteLine($"contested read: {read?.GetType().FullName}");
            read.Should().BeOfType<JsonElement>(
                "two subtypes in the build are named 'CompiledSharedItem'; binding either one would be "
                + "a guess that silently loses the other's members, so the content stays raw JSON");
            ((JsonElement)read!).GetProperty("item").GetProperty("label").GetString()
                .Should().Be("second family", "the raw JSON is kept verbatim");

            var uniqueJson = $$$"""
                {"$type":"{{{nameof(CompiledContestedContent)}}}","name":"B","item":{"$type":"{{{nameof(CompiledUniqueItem)}}}","code":"Y","rank":3}}
                """;
            var typed = JsonSerializer.Deserialize<object>(uniqueJson, Mesh.JsonSerializerOptions);
            typed.Should().NotBeNull();
            typed!.GetType().Should().BeSameAs(contested, "the content type resolves mesh-wide");
            var item = contested.GetProperty(nameof(CompiledContestedContent.Item))!.GetValue(typed);
            item!.GetType().Name.Should().Be(nameof(CompiledUniqueItem),
                "a name only one subtype in the build carries is unambiguous and still binds");
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>Loads this test assembly's bytes into a collectible context and registers the
    /// copy's content type on the mesh content-type map — what <c>MeshNodeHubFactory</c> does for a
    /// compiled NodeType. The mesh hub's own type registry is left untouched.</summary>
    private AssemblyLoadContext LoadCompiledCopy(string nodeTypePath, out Type compiledContent)
    {
        var file = typeof(CompiledSectionContent).Assembly.Location;
        file.Should().NotBeNullOrEmpty("the collectible copy is loaded from this assembly's file");
        var alc = new AssemblyLoadContext("compiled-abstract-member-" + Guid.NewGuid().ToString("N")[..8],
            isCollectible: true);
        Assembly copy;
        using (var stream = File.OpenRead(file))
            copy = alc.LoadFromStream(stream);
        compiledContent = copy.GetType(typeof(CompiledSectionContent).FullName!, throwOnError: true)!;
        compiledContent.Should().NotBeSameAs(typeof(CompiledSectionContent));
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

    private async Task<JsonElement> CoverOf(string path, CancellationToken ct)
    {
        // The authoritative node: the import has completed, so the node exists, and its NodeType
        // (DeclaredNodeType) activates a per-node hub through the default node configuration.
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        var json = JsonSerializer.Serialize(node!.Content, Mesh.JsonSerializerOptions);
        Output.WriteLine($"{path}: {json}");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("cover").Clone();
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
