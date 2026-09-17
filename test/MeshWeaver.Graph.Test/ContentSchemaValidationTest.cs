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

    /// <summary>The declared content shape of the NodeType under test.</summary>
    public record SchemaGuardedContent
    {
        /// <summary>A reference stored as a PATH — the member #4601 was measured on.</summary>
        public string? Country { get; init; }

        /// <summary>A plain label.</summary>
        public string? Label { get; init; }
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(GuardedType)
            {
                Name = "Schema Guarded",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<SchemaGuardedContent>())
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => "sg" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A node whose content is RAW JSON — the shape a payload has at the write boundary whenever
    /// the writing hub cannot type it, which for an in-mesh content type is always.
    /// </summary>
    private static MeshNode Guarded(string id, string contentJson) => new(id, TestPartition)
    {
        Name = "Guarded",
        NodeType = GuardedType,
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
}
