using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Pins the <c>select:</c> <b>projection contract</b> of the synced mesh node query
/// (<c>workspace.GetQuery</c>) — the one query qualifier that changes what a caller gets
/// back rather than merely how much is fetched.
///
/// <para>A synced query runs <c>Query&lt;MeshNode&gt;</c>, so a <c>select:</c> does NOT
/// project rows into <c>Dictionary&lt;string, object&gt;</c> the way it does for untyped
/// callers (that mismatch is what wedged every <c>select:</c>-carrying query on memex,
/// 2026-08-05; see <c>MeshQuery.ProjectSelect</c>). The row stays a <see cref="MeshNode"/>
/// and the SQL is narrowed instead — and on that path exactly ONE column is conditional:
/// <see cref="MeshNode.Content"/>.</para>
///
/// <para>That makes an omitted <c>content</c> a SILENT null rather than an error: the node
/// arrives fully formed, every other field intact, and only <c>ContentAs&lt;T&gt;()</c>
/// comes back empty. It reads exactly like "this node has no content" — the same symptom a
/// missing <c>TypeRegistry</c> entry produces, from a completely different cause. These
/// tests exist so that behaviour is a pinned contract instead of a rediscovered surprise.</para>
/// </summary>
public class SyncedQueryProjectionContractTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    // Isolated mesh per test method — each test owns its nodes AND its query ids (the
    // synced-query cache is keyed by (id, query set) and lives for the mesh's lifetime).
    protected override bool ShareMeshAcrossTests => false;

    private static readonly string Ns = $"{TestPartition}/ProjectionContract";
    private const string Body = "# Projected\n\nThe content column.";

    private static MeshNode Subject(string id) =>
        new(id, Ns)
        {
            Name = "Projected",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = Body },
        };

    private IObservable<IEnumerable<MeshNode>> Query(object id, string query)
        => Mesh.GetWorkspace().GetQuery(id, query);

    /// <summary>
    /// No <c>select:</c> at all — the conservative default. The whole node comes back,
    /// <see cref="MeshNode.Content"/> included and typed through the caller hub's options.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NoProjection_LoadsTypedContent()
    {
        var node = Subject("unprojected");
        await NodeFactory.CreateNode(node).Should().Emit();

        var row = await ReadOne($"$proj-none", $"namespace:{Ns} scope:subtree nodeType:Markdown", node.Path);

        row.Content.Should().NotBeNull(
            "an unprojected synced query is a whole-node read — the conservative default");
        row.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)!.Content
            .Should().Be(Body);
    }

    /// <summary>
    /// A <c>select:</c> that NAMES <c>content</c> behaves exactly like the unprojected read.
    /// This is the shape every content-bearing call site must use.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProjectionNamingContent_LoadsTypedContent()
    {
        var node = Subject("with-content");
        await NodeFactory.CreateNode(node).Should().Emit();

        var row = await ReadOne(
            "$proj-content",
            $"namespace:{Ns} scope:subtree nodeType:Markdown select:path,id,name,nodeType,content",
            node.Path);

        row.Name.Should().Be("Projected");
        row.Content.Should().NotBeNull(
            "`content` was named in the select: — the projection must fetch the column");
        row.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)!.Content
            .Should().Be(Body);
    }

    /// <summary>
    /// 🚨 THE TRAP, pinned. A <c>select:</c> that OMITS <c>content</c> still yields a
    /// well-formed <see cref="MeshNode"/> — right path, right name, right nodeType — whose
    /// <c>Content</c> is null. No error, no warning, no empty result set. Any consumer doing
    /// <c>ContentAs&lt;T&gt;()</c> on such a row silently renders empty.
    ///
    /// <para>If this test ever starts failing because <c>Content</c> came back populated, the
    /// projection stopped being honoured and the whole "select only what you need" guidance
    /// (and its cost saving on subtree reads) is dead — that is a real regression, not a
    /// harmless one.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProjectionOmittingContent_YieldsNodeWithNullContent()
    {
        var node = Subject("without-content");
        await NodeFactory.CreateNode(node).Should().Emit();

        var row = await ReadOne(
            "$proj-shell",
            $"namespace:{Ns} scope:subtree nodeType:Markdown select:path,id,name,nodeType",
            node.Path);

        // The metadata half is fully intact — which is exactly why the null below is so easy to miss.
        row.Name.Should().Be("Projected");
        row.NodeType.Should().Be("Markdown");

        row.Content.Should().BeNull(
            "a select: that does not name `content` makes the storage adapter project a null "
            + "content column — the node is well-formed and its Content is silently empty, which "
            + "is why every content-reading GetQuery MUST name `content` explicitly");
        row.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)
            .Should().BeNull("there is nothing to deserialise — and nothing reports that");
    }

    /// <summary>
    /// 🚨 A CALLER GETS THE QUERY IT ASKED FOR, even when it shares an id (issue #1311).
    ///
    /// <para>The cache used to be keyed by ID ALONE: <c>GetQuery(id, queries)</c> returned the
    /// already-registered stream and DISCARDED the queries on a hit, so a second call site
    /// sharing an id inherited the first registration's projection and its
    /// <c>ContentAs&lt;T&gt;()</c> read null. Which projection won was decided by subscription
    /// order — the mechanism behind "content is empty on some loads but not others".</para>
    ///
    /// <para>The same discard is what let a NodeType's newly declared
    /// <c>shared=@Other/Type/Source</c> be ignored until the portal restarted: the entry
    /// materialised before the edit kept answering the OLD source queries, so the compile saw a
    /// short-but-established source set and Roslyn emitted a <c>CS0103</c> about code that was
    /// present. The query set is now part of the key, so each distinct set gets its own shared
    /// collection.</para>
    ///
    /// <para>Sharing an id across differently-shaped reads is still poor practice — every
    /// distinct set keeps a resident subscription for the process's life — but it is no longer
    /// a silent wrong answer.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SharedId_WithADifferentProjection_StillHonoursTheQueryItWasAsked()
    {
        var node = Subject("shared-id");
        await NodeFactory.CreateNode(node).Should().Emit();

        const string id = "$proj-shared";

        // FIRST registration: shells only.
        var shell = await ReadOne(
            id, $"namespace:{Ns} scope:subtree nodeType:Markdown select:path,id,name,nodeType", node.Path);
        shell.Content.Should().BeNull();

        // SECOND call site, SAME id, explicitly asking for content.
        var second = await ReadOne(
            id, $"namespace:{Ns} scope:subtree nodeType:Markdown select:path,id,name,nodeType,content", node.Path);

        second.Content.Should().NotBeNull(
            "the query set is part of the cache key, so a caller that names `content` gets its "
            + "own collection instead of inheriting the first registration's shell projection");
        second.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)!.Content
            .Should().Be(Body);

        // …and the first call site is NOT evicted: its narrower registration still answers its
        // own query, so two callers cannot thrash each other's subscription.
        var shellAgain = await ReadOne(
            id, $"namespace:{Ns} scope:subtree nodeType:Markdown select:path,id,name,nodeType", node.Path);
        shellAgain.Content.Should().BeNull(
            "each distinct query set keeps its own entry — widening one caller's set must not "
            + "rewrite another's");
    }

    private async Task<MeshNode> ReadOne(object id, string query, string path) =>
        (await Query(id, query)
            .Where(nodes => nodes.Any(n => n.Path == path))
            .Should().Within(15.Seconds()).Emit())
        .Single(n => n.Path == path);
}
