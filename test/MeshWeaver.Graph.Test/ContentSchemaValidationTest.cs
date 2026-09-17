using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
}
