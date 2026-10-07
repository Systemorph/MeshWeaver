using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>TYPED NODE CONTENT IS VALIDATED AGAINST ITS DECLARED SHAPE ON WRITE</b> —
/// Systemorph/MeshWeaver#4601.
///
/// <para>Measured on memex.systemorph.com, 2026-09-17: a <c>patch</c> that put a JSON OBJECT into a
/// member declared <c>public string?</c> was accepted, versioned and stored VERBATIM. Nothing
/// errored. The record then no longer deserialised as its declared type, so every reader's
/// <c>ContentAs&lt;T&gt;</c> answered <c>null</c> — the page renders an empty node over a full
/// document (#4600), a reactive wait for the typed shape never completes, and there is nothing to
/// grep for. The same silence accepted a <c>Markdown</c> CREATE whose text sat under a member the
/// content type does not declare, so the node rendered empty from v1.</para>
///
/// <para>The content type here is registered through the NodeType's own hub configuration and on NO
/// other hub — deliberately, because that is the production shape: a compiled, in-mesh content type
/// (<c>Crm/Client</c>'s <c>ClientContent</c>) is never on the writing hub's <c>$type</c> registry,
/// which is exactly why <see cref="Security.ContentDiscriminatorValidator"/> exempts it and why the
/// payload reaches the write boundary as a raw <see cref="JsonElement"/>.</para>
///
/// <para>The third case is the CONTROL and the point of the narrow rule: content carrying an extra
/// member ALONGSIDE declared ones still lands. A guard that refused every unmapped member would be
/// a schema-strictness change, and legitimate writers — an older or newer writer of the same
/// record, a legacy field — produce that shape all the time.</para>
/// </summary>
public class ContentSchemaValidationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string GuardedType = "SchemaGuarded";
    private const string RequiredType = "SchemaRequired";
    private const string BufferedType = "SchemaBuffered";
    private const string CompiledType = "SchemaCompiled";

    /// <summary>The declared content shape of the NodeType under test.</summary>
    public record SchemaGuardedContent
    {
        /// <summary>A reference stored as a PATH — the member #4601 was measured on.</summary>
        public string? Country { get; init; }

        /// <summary>A plain label.</summary>
        public string? Label { get; init; }
    }

    /// <summary>
    /// A declared shape with a <c>required</c> member — the half #4624 documented and did not
    /// implement. Content that omits <see cref="Body"/> makes System.Text.Json raise a
    /// WHOLE-DOCUMENT failure (<c>JsonException.Path == "$"</c>) before any member is blamed, which
    /// is the shape <c>MarkdownContent.Content</c> has in production (#4648).
    /// </summary>
    public record RequiredMemberContent
    {
        /// <summary>The member every reader needs, and the one a partial write omits.</summary>
        public required string Body { get; init; }

        /// <summary>A plain label — declared, so content carrying it is not "nothing declared".</summary>
        public string? Label { get; init; }
    }

    /// <summary>
    /// The production shape the CD red was measured on: a <c>required</c> member AND a
    /// <c>[JsonExtensionData]</c> round-trip buffer, exactly like
    /// <see cref="Markdown.MarkdownContent"/>. The buffer is why unknown members are preserved
    /// rather than dropped, and therefore why the declared-member rule says nothing about them.
    /// </summary>
    public record BufferedRequiredContent
    {
        /// <summary>The required member — as <c>MarkdownContent.Content</c> is.</summary>
        public required string Body { get; init; }

        /// <summary>Round-trip buffer for members this shape does not declare.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public IDictionary<string, JsonElement>? UnknownMembers { get; init; }
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(GuardedType)
            {
                Name = "Schema Guarded",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<SchemaGuardedContent>())
            },
            new MeshNode(RequiredType)
            {
                Name = "Schema Required",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<RequiredMemberContent>())
            },
            new MeshNode(BufferedType)
            {
                Name = "Schema Buffered",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<BufferedRequiredContent>())
            },
            // The in-mesh shape of Plugins#3042's `Feedback/Feedback`: a NodeType whose definition
            // carries a runtime-compile SOURCE, which is what makes ContentDiscriminatorValidator
            // exempt it (its content types live on its own hub only). The delegate stands in for
            // the compiled configuration so the test needs no compiler.
            new MeshNode(CompiledType)
            {
                Name = "Schema Compiled",
                Content = new Configuration.NodeTypeDefinition
                {
                    Configuration = "// stands in for in-mesh source — the delegate below is what runs",
                },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<SchemaGuardedContent>())
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => "sg" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A node whose content is RAW JSON — the shape a payload has at the write boundary whenever
    /// the writing hub cannot type it, which for an in-mesh content type is always.
    /// </summary>
    private static MeshNode Guarded(string id, string contentJson) => Of(GuardedType, id, contentJson);

    /// <summary>The same shape for any of the NodeTypes this fixture declares.</summary>
    private static MeshNode Of(string nodeType, string id, string contentJson) => new(id, TestPartition)
    {
        Name = "Guarded",
        NodeType = nodeType,
        State = MeshNodeState.Active,
        Content = JsonSerializer.Deserialize<JsonElement>(contentJson),
    };

    [Fact(Timeout = 180_000)]
    public async Task Create_WithAMemberContradictingItsDeclaredType_IsRefused_NamingTheMemberAndTheType()
    {
        var id = NewId();

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Guarded(id, """{"country":{"$type":"CountryReference","id":"CH"},"label":"ok"}"""))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "content that cannot bind to its declared type must be refused at the write boundary — "
            + "storing it turns the caller's mistake into durable corruption that only surfaces "
            + "months later as an empty page (#4601)");
        failure!.Message.Should().Contain("country",
            "a refusal that does not name the offending member leaves the caller guessing");
        failure.Message.Should().Contain("string",
            "the refusal must name the type the member was declared as");
        failure.Message.Should().Contain("object",
            "…and what was sent instead");

        // Nothing was written: the refusal precedes the store, so the path stays absent.
        var listed = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{TestPartition} nodeType:{GuardedType}"))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);
        listed.Items.Should().NotContain(n => n.Id == id,
            "a refused create must leave no node behind");
    }

    [Fact(Timeout = 180_000)]
    public async Task Create_WithContentNoneOfWhoseMembersAreDeclared_IsRefused()
    {
        var id = NewId();

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Guarded(id, """{"markdown":"# a whole document"}"""))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "UnmappedMemberHandling.Skip makes this bind CLEANLY to an instance carrying none of "
            + "the authored data — no exception can catch it, so the guard must count members "
            + "(this is how the #4600 node was born empty at v1)");
        failure!.Message.Should().Contain("markdown",
            "the refusal must name the members that matched nothing");
        failure.Message.Should().Contain("country",
            "…and say what the declared members are, so the caller can fix the payload in one retry");
    }

    [Fact(Timeout = 180_000)]
    public async Task Create_WithAnExtraMemberAlongsideDeclaredOnes_StillLands()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Guarded(id, """{"country":"Crm/Country/CH","legacyExtra":1}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "content carrying an unmapped member ALONGSIDE declared ones is what an older or "
                + "newer writer of the same record produces — refusing it would be a "
                + "schema-strictness change, not this fix",
                cancellationToken: TestContext.Current.CancellationToken);

        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
        stored.ContentAs<SchemaGuardedContent>(Mesh.JsonSerializerOptions)!.Country
            .Should().Be("Crm/Country/CH", "the declared member must have survived the write");
    }

    [Fact(Timeout = 180_000)]
    public async Task Update_WithAMemberContradictingItsDeclaredType_IsRefused_AndLeavesTheNodeAsItWas()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Guarded(id, """{"country":"Crm/Country/CH","label":"before"}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "the node to corrupt must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(Guarded(id, """{"country":{"$type":"CountryReference","id":"DE"},"label":"after"}"""))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "the MCP patch path merges and then calls UpdateNode, which is the surface #4601 was "
            + "measured on");
        failure!.Message.Should().Contain("country");

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
        after.ContentAs<SchemaGuardedContent>(Mesh.JsonSerializerOptions)!.Country
            .Should().Be("Crm/Country/CH", "a refused update must leave the node as it was");
    }

    /// <summary>
    /// 🚨 THE CD RED (Systemorph/MeshWeaver#4648). Content that omits a <c>required</c> member
    /// raises a WHOLE-DOCUMENT <see cref="JsonException"/> (<c>Path == "$"</c>), which the guard
    /// documents as not judged. It was caught behind a <c>when</c> filter that did not match, so
    /// the exception ESCAPED the validator and failed the write — the agent's <c>update</c> tool
    /// answered with the raw serializer text instead of "Updated:", and two
    /// <c>MeshPluginTest</c> cases red core's Continuous Delivery on
    /// <c>MarkdownContent.Content</c>, a member declared exactly this way.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Create_OmittingARequiredMember_StillLands_WhenAnotherDeclaredMemberIsPresent()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Of(RequiredType, id, """{"label":"partial"}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "a missing `required` member is the ordinary partial-content shape a legitimate "
                + "writer produces — this guard says nothing about it, and 'says nothing' must mean "
                + "the write LANDS, not that a JsonException escapes and fails it (#4648)",
                cancellationToken: TestContext.Current.CancellationToken);

        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
        stored.Content.Should().NotBeNull("the content the caller sent must be what was stored");
    }

    /// <summary>
    /// 🚨 The same exemption on the UPDATE path — which is the surface the CD red was actually
    /// measured on (`MeshPlugin.Update` -> `MeshOperations.Update` -> `mesh.UpdateNode`). Update
    /// reaches the guard with a different <see cref="NodeValidationContext"/> (it carries
    /// <c>ExistingNode</c>, and runs the byte-identical shortcut first), so a create-only
    /// regression suite would stay green while the update path reintroduced the failure
    /// (automatic review on #4657).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Update_OmittingARequiredMember_StillLands_AndChangesTheNode()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Of(RequiredType, id, """{"body":"before","label":"before"}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "the node to update must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        // The caller sends only what it is changing — `body` is `required` and absent, which is the
        // whole-document bind failure. The content also DIFFERS from what is stored, so the
        // byte-identical shortcut cannot be what carries this case.
        await MeshService.UpdateNode(Of(RequiredType, id, """{"label":"after"}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "an update that omits a `required` member is the partial-content shape the guard "
                + "declines to judge — on main the JsonException escaped and failed it, which is "
                + "exactly what red MeshPluginTest's two update cases in CD (#4648)",
                cancellationToken: TestContext.Current.CancellationToken);

        // Terminal state, and it discriminates: BEFORE the update the stored content binds
        // (Label == "before"); after it, `body` is gone and it cannot bind at all. Waiting for the
        // bind to STOP succeeding is therefore waiting for this specific write, not for any write.
        // That it no longer binds is the exemption's documented cost, not a surprise — see
        // Doc/Architecture/ContentSchemaOnWrite, "What the exemption costs".
        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null
                        && n.ContentAs<RequiredMemberContent>(Mesh.JsonSerializerOptions) is null)
            .FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);

        // Serialise rather than cast: the stored value's representation is not this test's business
        // (AGENTS.md — never cast an object payload), and this reads the same either way.
        var stored = JsonSerializer.SerializeToElement(after.Content, Mesh.JsonSerializerOptions);
        stored.GetProperty("label").GetString().Should().Be("after",
            "the caller's value must be what landed");
        stored.TryGetProperty("body", out _).Should().BeFalse(
            "a full-replacement update writes what the caller sent — the omitted member is not "
            + "silently re-filled from the previous version");
    }

    /// <summary>
    /// The counterpart, and the reason the whole-document failure FALLS THROUGH rather than
    /// returning Valid: a declared type with a <c>required</c> member throws before
    /// <see cref="JsonUnmappedMemberHandling.Skip"/> can apply, so returning Valid on <c>$</c>
    /// would exempt every such type from the declared-member rule — the one that catches the
    /// <c>{"markdown":"…"}</c>-on-a-Markdown-node shape #4601 was filed for.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Create_OnATypeWithARequiredMember_IsStillRefused_WhenNoMemberIsDeclared()
    {
        var id = NewId();

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Of(RequiredType, id, """{"markdown":"# a whole document"}"""))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "content none of whose members the declared type knows is refused whether the bind "
            + "failed as a whole or skipped its way to an empty instance");
        failure!.Message.Should().Contain("markdown",
            "the refusal must name the members that matched nothing");
        failure.Message.Should().Contain("body",
            "…and say what the declared members are");
        failure.Message.Should().NotContain("required properties",
            "the caller must get the guard's own localized refusal, never the serializer's raw "
            + "English text leaking out of an escaped exception (#4648)");
    }

    /// <summary>
    /// The production shape of the CD red, member for member: a <c>required</c> member AND a
    /// <c>[JsonExtensionData]</c> buffer, as <see cref="Markdown.MarkdownContent"/> declares. The
    /// buffer PRESERVES the unknown member, so the declared-member rule exempts it and the write
    /// lands — which is what <c>MeshPluginTest.Update_ExistingNode_UpdatesSuccessfully</c> asserts.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Create_OnABufferedRequiredType_Lands_WhenTheContentIsAllUnknownMembers()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Of(BufferedType, id, """{"text":"updated"}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "an extension-data buffer round-trips what the shape does not declare, so nothing "
                + "is silently dropped and there is nothing for this guard to refuse — the MarkdownContent "
                + "shape the CD red was measured on (#4648)",
                cancellationToken: TestContext.Current.CancellationToken);

        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
        stored.Content.Should().NotBeNull("the write must have landed with its content");
    }

    // ── Systemorph/MeshWeaver.Plugins#3042: a `$type` that names NO type, and the Create verb's
    //    silent member drop ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE DEAD LETTER (Systemorph/MeshWeaver.Plugins#3042). An agent created
    /// <c>Feedback/Feedback</c> nodes as <c>{"$type":"Feedback","status":…,"description":…}</c> —
    /// no type named <c>Feedback</c> exists anywhere; the NodeType binds <c>FeedbackContent</c>.
    /// The discriminator guard exempts every runtime-compiled NodeType and this guard admitted any
    /// <c>$type</c> naming a different record, so the write was stored and every reader's
    /// <c>ContentAs</c> answered null. The production shape is reproduced member for member: a
    /// foreign, nonexistent <c>$type</c> beside a DECLARED member (so the all-unknown rule cannot be
    /// what refuses it) and an undeclared one.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Create_WhoseTypeDiscriminatorNamesNoType_IsRefused_NamingItAndTheDeclaredType()
    {
        var id = NewId();

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Of(CompiledType, id, """{"$type":"Feedback","label":"Bug","description":"what broke"}"""))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "a `$type` that resolves to no type is not 'a different record' — it is a record that "
            + "does not exist; stored, it reads as empty everywhere and every watcher of the NodeType "
            + "skips it without a word (Plugins#3042)");
        failure!.Message.Should().Contain("'Feedback'", "the refusal must name the discriminator it could not resolve");
        failure.Message.Should().Contain(nameof(SchemaGuardedContent), "…and the content type the NodeType declares");
    }

    /// <summary>
    /// The same dead letter written as the as-written <see cref="System.Text.Json.Nodes.JsonObject"/>
    /// DOM instead of a <see cref="JsonElement"/>. Both are raw JSON; the guard used to judge only
    /// the latter, so a direct Create carrying the JsonObject shape skipped the rule entirely.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Create_AsJsonObject_WhoseTypeDiscriminatorNamesNoType_IsRefused()
    {
        var id = NewId();
        var node = Of(CompiledType, id, "{}") with
        {
            Content = System.Text.Json.Nodes.JsonNode.Parse("""{"$type":"Feedback","label":"Bug","description":"what broke"}""")!.AsObject(),
        };

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(node)
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "a JsonObject is the same raw JSON as a JsonElement — its shape must not exempt it from the guard");
        failure!.Message.Should().Contain("'Feedback'");
        failure.Message.Should().Contain(nameof(SchemaGuardedContent));
    }

    /// <summary>
    /// The CONTROL for the rule above: a <c>$type</c> naming a DIFFERENT record that EXISTS (here a
    /// type compiled alongside the declared one — the polymorphic-subtype shape) is still admitted,
    /// exactly as before. Without it the test above would pass for a guard that refused every
    /// foreign discriminator, which is the reshaping this guard deliberately does not do.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Create_WhoseTypeDiscriminatorNamesAnExistingOtherType_StillLands()
    {
        var id = NewId();

        await MeshService.CreateNode(Of(CompiledType, id, $$"""{"$type":"{{nameof(RequiredMemberContent)}}","body":"b"}"""))
            .Take(1).Should().Within(60.Seconds()).Emit(
                "a discriminator that resolves to a real type is the discriminator guard's case, not "
                + "this one's — judging it by the declared type would be reshaping it",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The MCP <c>create</c> verb on the same shape. Its own schema check deserialised into the
    /// bound type — which ignores a foreign <c>$type</c> and skips unmapped members — so it answered
    /// <c>Created:</c>. The verb now judges the RAW content on the probe hub, the one place an
    /// in-mesh content type is known (on this facade's hub the content is a JsonElement).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task McpCreate_WhoseTypeDiscriminatorNamesNoType_IsRefusedBeforeTheWrite()
    {
        var id = NewId();

        var answer = await new MeshOperations(Mesh)
            .Create(NodeJson(id, """{"$type":"Feedback","label":"Bug"}"""))
            .FirstAsync().Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"create answered: {answer}");
        answer.Should().StartWith("Error: refused create",
            "the verb must refuse on its OWN check, before the write boundary — that is what names "
            + "the remedy in the tool's answer");
        answer.Should().Contain("'Feedback'").And.Contain(nameof(SchemaGuardedContent))
            .And.Contain("Nothing was written");
    }

    /// <summary>
    /// The reverse direction from the same incident: the RIGHT shape plus a member the bound type
    /// does not declare (<c>description</c>) was created and the member silently dropped. Patch and
    /// Update already refuse that; Create now does too, naming the member.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task McpCreate_WithAnUndeclaredContentMember_IsRefusedNamingIt()
    {
        var id = NewId();

        var answer = await new MeshOperations(Mesh)
            .Create(NodeJson(id, """{"label":"Bug","description":"what broke"}"""))
            .FirstAsync().Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"create answered: {answer}");
        answer.Should().StartWith("Error: refused create");
        answer.Should().Contain("'description'").And.Contain(nameof(SchemaGuardedContent));
        answer.Should().NotContain("'label'", "label IS declared and must not be named");
    }

    /// <summary>The CONTROL for both MCP cases: the declared shape is created as before.</summary>
    [Fact(Timeout = 180_000)]
    public async Task McpCreate_WithTheDeclaredShape_IsCreated()
    {
        var id = NewId();

        var answer = await new MeshOperations(Mesh)
            .Create(NodeJson(id, $$"""{"$type":"{{nameof(SchemaGuardedContent)}}","label":"Bug","country":"Crm/Country/CH"}"""))
            .FirstAsync().Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"create answered: {answer}");
        answer.Should().StartWith("Created:");
    }

    private static string NodeJson(string id, string contentJson)
        => $$"""{"id":"{{id}}","namespace":"{{TestPartition}}","name":"Guarded","nodeType":"{{CompiledType}}","content":{{contentJson}}}""";
}
