using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The grant-rule check matches a rule's <see cref="GrantRule.ContentField"/> against the
/// partition root's content AS STORED, never against a re-typed copy (MeshWeaver#7426).</b>
///
/// <para>The Postgres projection matches the stored JSON (<c>content-&gt;&gt;field</c>). The
/// in-process fold read the root through the node-stream cache, which deserialised it into its
/// registered type; <see cref="GrantRuleEvaluator"/> then re-serialised that object. The round trip
/// is lossy in two directions, and each test below stores one of them as a LEGACY-shaped row (raw
/// JSON, as an older serializer or an untyped write left it):</para>
/// <list type="bullet">
///   <item><b>(a)</b> the stored JSON has no <c>type</c>, but the CLR member's initializer is the
///     rule's value — the re-typed copy GRANTED where the stored JSON (and Postgres) does not;</item>
///   <item><b>(b)</b> the stored JSON holds an explicit <c>"approved": false</c>, a CLR default the
///     serializer omits on the way back — the re-typed copy DENIED where Postgres grants.</item>
/// </list>
/// <para>The root query now carries <see cref="ParsedQuery.StoredContentQualifier"/>, the cache
/// passes such a result through untouched, and both evaluators read the same input. The Postgres
/// half of the parity (the provider honouring the flag) is pinned in MeshWeaver.Plugins.</para>
///
/// <para><b>Negative control:</b> <see cref="AModernRow_GetsTheSameVerdict_EitherWay"/> writes the
/// same type TYPED — the shape the current serializer stores — and passes with and without the
/// qualifier; (a) and (b) fail without it (measured: the qualifier removed from
/// <see cref="SecurityQueries.PartitionRoot"/>).</para>
/// </summary>
public class GrantRuleStoredContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CounterpartyType = "Crm/Counterparty";
    private const string ClientType = "Crm/CounterpartyType/Client";
    private const string SalesMembers = "Sales/Members";
    private const string Alice = "stored-alice";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    // ConfigureMeshBase: the base ConfigureMesh grants Public Admin, which would make every verdict
    // true for the wrong reason.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .WithMeshType(typeof(StoredContentCounterparty), nameof(StoredContentCounterparty))
            .AddMeshNodes(new MeshNode($"{Alice}_Membership", SalesMembers)
            {
                NodeType = "GroupMembership",
                Content = new GroupMembership { Member = Alice, Groups = [new MembershipEntry { Group = SalesMembers }] },
            });

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>A root whose content is the given JSON, stored exactly as written (a legacy row).</summary>
    private Task<MeshNode?> WriteLegacyRoot(string partition, string json)
        => Storage.Write(new MeshNode(partition)
            {
                NodeType = CounterpartyType,
                Name = partition,
                Content = JsonDocument.Parse(json).RootElement.Clone(),
            }, Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit($"the root {partition} must be stored",
                cancellationToken: TestContext.Current.CancellationToken);

    private Task<MeshNode?> WriteTypedRoot(string partition, StoredContentCounterparty content)
        => Storage.Write(new MeshNode(partition) { NodeType = CounterpartyType, Name = partition, Content = content },
                Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit($"the root {partition} must be stored",
                cancellationToken: TestContext.Current.CancellationToken);

    private Task<MeshNode?> WriteRule(string id, string field, string value)
        => Storage.Write(new MeshNode(id, GrantRule.Namespace)
            {
                NodeType = GrantRule.NodeTypeName,
                Name = id,
                Content = new GrantRule
                {
                    NodeType = CounterpartyType,
                    ContentField = field,
                    ContentValue = value,
                    AccessObject = SalesMembers,
                    Role = Role.Editor.Id,
                },
            }, Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit($"the rule {id} must be stored",
                cancellationToken: TestContext.Current.CancellationToken);

    private Task<Permission> Settle(string path, Func<Permission, bool> until, string because)
        => PermissionEvaluator.GetEffectivePermissions(Mesh, path, Alice)
            .Where(until)
            .Take(1)
            .Should().Within(Budget).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    private Task<Permission> First(string path)
        => Settle(path, _ => true, "the fold must answer");

    private static bool IsEditor(Permission p)
        => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Update) && p.HasFlag(Permission.Create);

    private const string TypeName = nameof(StoredContentCounterparty);

    [Fact]
    public async Task AFieldAbsentFromTheStoredJson_NeverMatches_ThoughItsInitializerWould()
    {
        var control = Unique("StoredClient");
        var legacy = Unique("StoredLegacy");
        await WriteLegacyRoot(control, $$"""{"$type":"{{TypeName}}","type":"{{ClientType}}","owner":"x"}""");
        await WriteLegacyRoot(legacy, $$"""{"$type":"{{TypeName}}","owner":"x"}""");
        await WriteRule("stored-type", "type", ClientType);

        await Settle(control, IsEditor, "control: a stored type == Client matches");

        (await First(legacy)).Should().Be(Permission.None,
            "the stored JSON carries no `type`, so Postgres' content->>'type' is NULL and the rule does "
            + "not apply; the in-process check must not read the CLR initializer in its place");
    }

    [Fact]
    public async Task AnExplicitDefaultInTheStoredJson_Matches_ThoughTheSerializerWouldOmitIt()
    {
        var legacy = Unique("StoredFalse");
        await WriteLegacyRoot(legacy, $$"""{"$type":"{{TypeName}}","type":"Crm/CounterpartyType/Partner","approved":false}""");
        await WriteRule("stored-unapproved", "approved", "false");

        IsEditor(await Settle(legacy, IsEditor,
                "the stored JSON holds approved:false, which Postgres matches as 'false'; the in-process "
                + "check must see the same field, not a re-serialised copy that omits a CLR default"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task AModernRow_GetsTheSameVerdict_EitherWay()
    {
        var modern = Unique("StoredModern");
        var other = Unique("StoredModernPartner");
        await WriteTypedRoot(modern, new StoredContentCounterparty { Type = ClientType, Owner = "x" });
        await WriteTypedRoot(other, new StoredContentCounterparty { Type = "Crm/CounterpartyType/Partner", Owner = "x" });
        await WriteRule("stored-modern", "type", ClientType);

        await Settle(modern, IsEditor, "a typed root carrying type == Client matches");
        (await First(other)).Should().Be(Permission.None, "and a typed Partner root does not");
    }
}

/// <summary>
/// A partition-root content type whose round trip is lossy: <see cref="Type"/> has a NON-default
/// initializer (absent in JSON ⇒ the initializer once typed), and <see cref="Approved"/> is a bool
/// whose CLR default the serializer omits on the way back.
/// </summary>
public record StoredContentCounterparty
{
    /// <summary>The counterparty kind. Defaults to the value the rule under test names.</summary>
    public string Type { get; init; } = "Crm/CounterpartyType/Client";

    /// <summary>Whether the counterparty is approved.</summary>
    public bool Approved { get; init; }

    /// <summary>The owner.</summary>
    public string? Owner { get; init; }
}
