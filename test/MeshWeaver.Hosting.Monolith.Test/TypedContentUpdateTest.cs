using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Pins <c>MeshNodeStreamHandle.Update&lt;TContent&gt;</c> — the typed write where the CALLER
/// names the content type, so nothing has to resolve a <c>$type</c> discriminator to a CLR type.
///
/// <para>The untyped <c>Update(node =&gt; …)</c> leaves every writer to re-derive the content type
/// itself, and the idiom that spread through the codebase is
/// <c>node.Content as TContent ?? new TContent()</c>. That is a silent data-loss bug: when content
/// arrives as JSON (file-system / Postgres / any cross-hub read) the cast is <c>null</c>, the
/// <c>?? new()</c> materialises a DEFAULT record, and the write persists those defaults over every
/// field the caller never touched. <see cref="UntypedUpdate_WithTheAsOrDefaultIdiom_SilentlyWritesDefaultsOverRealFields"/>
/// reproduces exactly that; the two <c>Update&lt;TContent&gt;</c> tests pin the cure.</para>
///
/// <para>And the cure must not trade one silent failure for another: a node whose content is
/// PRESENT but unreadable as <typeparamref name="TContent"/> must <b>fail the write loudly</b>,
/// never arrive as <c>null</c> for the caller to <c>?? new()</c> again. That is
/// <see cref="TypedUpdate_UnconvertibleContent_FaultsAndLeavesTheNodeUntouched"/>.</para>
/// </summary>
public class TypedContentUpdateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>Creates a Markdown node and returns its activated per-node workspace.</summary>
    private async Task<(string Path, IWorkspace Workspace)> CreateNodeAsync(string id, object? content)
    {
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = "Markdown",
            Content = content,
        }).Should().Emit();

        // Per-node hubs activate lazily — any inbound message brings the hub up.
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("the GetDataRequest above must have activated the hub");
        return (path, nodeHub!.GetWorkspace());
    }

    /// <summary>
    /// Content with NO <c>$type</c> discriminator — the "as-written DOM" shape
    /// (<c>ObjectAsExtensions</c> case 2): application code and repo content files build content as
    /// a plain JSON object, and it is forwarded verbatim until something re-types it. With no
    /// discriminator the polymorphic reader has nothing to resolve, so it hands back a bare
    /// <see cref="JsonElement"/> — and that is precisely when <c>Content as T</c> starts returning
    /// <c>null</c>.
    /// <para>Note what this fixture is NOT: a JsonElement of a type the hub DOES know. Those are
    /// re-typed on the way in, which is why the <c>as T ?? new T()</c> idiom looks safe in most
    /// tests and still destroys data in the field. It is also deliberately not an UNREGISTERED
    /// <c>$type</c>: <c>ContentDiscriminatorValidator</c> rejects those at Create, so that shape
    /// cannot reach a stored node in the first place.</para>
    /// </summary>
    private const string UnresolvableContentJson =
        """{"content":"# real","prerenderedHtml":"<h1>real</h1>"}""";

    /// <summary>
    /// 🚨 THE DEFECT, reproduced with no mocks and no Roslyn: unresolvable JSON content, the
    /// <c>as T ?? new T()</c> idiom, and a write that destroys a field the caller never named.
    /// Passes before and after — it documents WHY the typed overload exists, and it is the exact
    /// input <see cref="TypedUpdate_UnresolvableJsonContent_ConvertsAndPreservesUntouchedFields"/>
    /// survives.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task UntypedUpdate_WithTheAsOrDefaultIdiom_SilentlyWritesDefaultsOverRealFields()
    {
        var options = Mesh.JsonSerializerOptions;
        var stored = JsonSerializer.Deserialize<JsonElement>(UnresolvableContentJson);
        var (path, workspace) = await CreateNodeAsync("untyped-idiom", stored);

        // The idiom, verbatim as it appears across the codebase.
        await workspace.GetMeshNodeStream(path)
            .Update(node =>
            {
                var c = node.Content as MarkdownContent ?? new MarkdownContent { Content = "" };
                return node with { Content = c with { Content = "# edited" } };
            })
            .FirstAsync().ToTask();

        var after = await workspace.GetMeshNodeStream(path)
            .Where(n => n.ContentAs<MarkdownContent>(options)?.Content == "# edited")
            .FirstAsync().Timeout(10.Seconds()).ToTask();

        after.ContentAs<MarkdownContent>(options)!.PrerenderedHtml.Should().BeNull(
            "this is the defect: `as T` was null on unresolvable JSON content, `?? new T()` "
            + "supplied a DEFAULT record, and the write persisted that default over "
            + "PrerenderedHtml — a field the caller never mentioned.");
    }

    /// <summary>
    /// The cure, on the SAME input: the caller names the type, so the content is converted by
    /// <c>ContentAs&lt;T&gt;</c> — no name→Type lookup anywhere — and the field the lambda does not
    /// touch survives the write.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TypedUpdate_UnresolvableJsonContent_ConvertsAndPreservesUntouchedFields()
    {
        var options = Mesh.JsonSerializerOptions;
        var stored = JsonSerializer.Deserialize<JsonElement>(UnresolvableContentJson);
        var (path, workspace) = await CreateNodeAsync("typed-unresolvable", stored);

        await workspace.GetMeshNodeStream(path)
            .Update<MarkdownContent>(c => c with { Content = "# edited" })
            .FirstAsync().ToTask();

        var after = await workspace.GetMeshNodeStream(path)
            .Where(n => n.ContentAs<MarkdownContent>(options)?.Content == "# edited")
            .FirstAsync().Timeout(10.Seconds()).ToTask();

        after.ContentAs<MarkdownContent>(options)!.PrerenderedHtml.Should().Be("<h1>real</h1>",
            "Update<TContent> read the unresolvable JSON AS MarkdownContent, so the untouched "
            + "field round-tripped instead of being replaced by a default — the same input the "
            + "untyped idiom destroys");
    }

    /// <summary>
    /// The typed write also handles content that is ALREADY a typed instance of a statically
    /// registered type — the common case — without a round-trip surprise.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TypedUpdate_JsonContent_ConvertsAndPreservesUntouchedFields()
    {
        var options = Mesh.JsonSerializerOptions;
        var stored = JsonSerializer.SerializeToElement(
            new MarkdownContent { Content = "# real", PrerenderedHtml = "<h1>real</h1>" },
            typeof(MarkdownContent), options);
        var (path, workspace) = await CreateNodeAsync("typed-json", stored);

        await workspace.GetMeshNodeStream(path)
            .Update<MarkdownContent>(c => c with { Content = "# edited" })
            .FirstAsync().ToTask();

        var after = await workspace.GetMeshNodeStream(path)
            .Where(n => n.ContentAs<MarkdownContent>(options)?.Content == "# edited")
            .FirstAsync().Timeout(10.Seconds()).ToTask();

        var content = after.ContentAs<MarkdownContent>(options)!;
        content.Content.Should().Be("# edited");
        content.PrerenderedHtml.Should().Be("<h1>real</h1>",
            "Update<TContent> read the JsonElement AS MarkdownContent, so the untouched field "
            + "round-tripped instead of being replaced by a default");
    }

    /// <summary>
    /// 🚨 The loud-failure contract. <c>ContentAs&lt;T&gt;</c> returns null for unconvertible
    /// content because a READ must be bad-data tolerant. A WRITE must not: a null there would let
    /// the caller write a fresh default over the real record. So the write faults, and the node is
    /// left exactly as it was.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TypedUpdate_UnconvertibleContent_FaultsAndLeavesTheNodeUntouched()
    {
        var options = Mesh.JsonSerializerOptions;
        // A typed value of a DIFFERENTLY-named type: ContentAs<MarkdownContent> returns null for
        // this by contract (probe-dispatch call sites depend on that), so it is precisely the case
        // a write must refuse rather than "recover".
        var (path, workspace) = await CreateNodeAsync(
            "typed-wrong", new Comment { Text = "not markdown content" });

        var act = () => workspace.GetMeshNodeStream(path)
            .Update<MarkdownContent>(c => c with { Content = "# edited" })
            .FirstAsync().ToTask();

        var ex = await act.Should().ThrowAsync<MeshNodeStreamException>(
            "a write whose content cannot be read as the named type must fail loudly, never write "
            + "a default-valued record over the caller's real content");
        ex.Which.Error.Code.Should().Be(MeshNodeErrorCode.Deserialization);
        ex.Which.Error.Path.Should().Be(path);

        // …and the node is untouched: the refusal happened BEFORE any write.
        var after = await workspace.GetMeshNodeStream(path).FirstAsync().Timeout(10.Seconds()).ToTask();
        after.ContentAs<Comment>(options)!.Text.Should().Be("not markdown content",
            "the refused write must not have replaced the real content");
    }

    /// <summary>
    /// 🚨 The same refusal on the CROSS-HUB write path. Most writes in the mesh are not own-hub
    /// writes — they route through <c>IMeshNodeStreamCache</c> to the owning per-node hub, and that
    /// path invokes the update lambda on the cache's serial queue, several hops from the caller. A
    /// guarantee that only holds on the own path is not a guarantee: a lambda exception swallowed
    /// there would report success while writing nothing, or worse, write the default.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TypedUpdate_UnconvertibleContent_FaultsOnTheCrossHubPathToo()
    {
        var (path, _) = await CreateNodeAsync(
            "typed-wrong-remote", new Comment { Text = "not markdown content" });

        // The MESH workspace is NOT the node's owner — this write routes through the cache.
        var foreign = Mesh.GetWorkspace();
        var act = () => foreign.GetMeshNodeStream(path)
            .Update<MarkdownContent>(c => c with { Content = "# edited" })
            .FirstAsync().ToTask();

        var ex = await act.Should().ThrowAsync<MeshNodeStreamException>(
            "the refusal must survive the cache's serial queue and the remote write path");
        ex.Which.Error.Code.Should().Be(MeshNodeErrorCode.Deserialization);
    }

    /// <summary>
    /// Absence is NOT an error and is distinguishable from "could not be read": the node+content
    /// overload passes <c>null</c> only when the node genuinely has no content, so a caller can
    /// initialise it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TypedUpdate_AbsentContent_YieldsNullSoTheCallerCanInitialise()
    {
        var options = Mesh.JsonSerializerOptions;
        var (path, workspace) = await CreateNodeAsync("typed-absent", content: null);

        var sawNull = false;
        await workspace.GetMeshNodeStream(path)
            .Update<MarkdownContent>((node, content) =>
            {
                sawNull = content is null;
                return node with { Content = new MarkdownContent { Content = "# created" } };
            })
            .FirstAsync().ToTask();

        sawNull.Should().BeTrue("null must mean ABSENT — the one case a caller may initialise");

        var after = await workspace.GetMeshNodeStream(path)
            .Where(n => n.ContentAs<MarkdownContent>(options) is not null)
            .FirstAsync().Timeout(10.Seconds()).ToTask();
        after.ContentAs<MarkdownContent>(options)!.Content.Should().Be("# created");
    }
}
