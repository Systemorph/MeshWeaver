using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A declared, type-scoped grant (<see cref="GrantRule"/>) is a role at the partition ROOT —
/// computed by the fold, never written as an <c>AccessAssignment</c></b>.
///
/// <para>The shape this pins is the first real use: every partition whose root is a
/// <c>Crm/Counterparty</c> with <c>content.type == Crm/CounterpartyType/Client</c> grants the group
/// <c>Sales/Members</c> the <c>Editor</c> role. Each test writes the root and the rule into the
/// store and asks the fold — no grant row exists anywhere, so a pass can only come from the rule.</para>
///
/// <para><b>Negative control,</b> run before committing: with the <c>ObserveRuleGrants</c> leg's
/// result not added to the granted map in <c>PermissionEvaluator</c>, every test asserting a grant
/// fails (the fold answers <see cref="Permission.None"/>), while the tests asserting its ABSENCE —
/// non-matching type, deny, the outsider — pass either way, which is why each of those first
/// proves the rule DOES apply to a sibling in the same mesh.</para>
/// </summary>
public class DeclaredGrantRuleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CounterpartyType = "Crm/Counterparty";
    private const string ClientType = "Crm/CounterpartyType/Client";
    private const string PartnerType = "Crm/CounterpartyType/Partner";
    private const string SalesMembers = "Sales/Members";
    private const string SalesLeads = "Sales/Leads";

    /// <summary>A direct member of Sales/Members.</summary>
    private const string Alice = "rule-alice";
    /// <summary>A member of Sales/Leads, which is itself a member of Sales/Members (nesting).</summary>
    private const string Nora = "rule-nora";
    /// <summary>In no group.</summary>
    private const string Olaf = "rule-olaf";

    /// <summary>A client partition that carries a DENY of Editor for Alice.</summary>
    private const string DeniedClient = "RuleDeniedClient";

    private const string RepoUrl = "https://github.com/test/rule-owned";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public Admin everywhere,
    // which would make every verdict below true for the wrong reason.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddGitHubSyncTypes()
            .WithMeshType(typeof(RuleTestCounterparty), nameof(RuleTestCounterparty))
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            })
            .AddMeshNodes(
                Membership(Alice, SalesMembers),
                Membership(Nora, SalesLeads),
                Membership(SalesLeads, SalesMembers),
                AssignmentNodeFactory.UserRole(Alice, Role.Editor.Id, DeniedClient, denied: true));

    private static MeshNode Membership(string member, string group) =>
        new($"{member.Replace('/', '_')}_Membership", group)
        {
            NodeType = "GroupMembership",
            Content = new GroupMembership { Member = member, Groups = [new MembershipEntry { Group = group }] },
        };

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid().ToString("N")[..8]}";

    // A REGISTERED content type, as a compiled NodeType's content is on a real mesh: an
    // unregistered JsonElement root would be a degraded-content finding of its own.
    private static RuleTestCounterparty Content(string type) => new() { Type = type, Owner = "someone" };

    private Task<MeshNode?> WriteRoot(string partition, string type, string nodeType = CounterpartyType)
        => Storage.Write(new MeshNode(partition) { NodeType = nodeType, Name = partition, Content = Content(type) },
                Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit($"the root {partition} must be stored",
                cancellationToken: TestContext.Current.CancellationToken);

    private Task<MeshNode?> WriteRule(string id, string subject, string role, string? contentValue = ClientType)
        => Storage.Write(new MeshNode(id, GrantRule.Namespace)
            {
                NodeType = GrantRule.NodeTypeName,
                Name = id,
                Content = new GrantRule
                {
                    NodeType = CounterpartyType,
                    ContentField = contentValue is null ? null : "type",
                    ContentValue = contentValue,
                    AccessObject = subject,
                    Role = role,
                },
            }, Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit($"the rule {id} must be stored",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The fold's verdict for <paramref name="user"/> at <paramref name="path"/>, once <paramref name="until"/> holds.</summary>
    private Task<Permission> Settle(string path, string user, Func<Permission, bool> until, string because)
        => PermissionEvaluator.GetEffectivePermissions(Mesh, path, user)
            .Where(until)
            .Take(1)
            .Should().Within(Budget).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The fold's FIRST verdict — what every one-shot gate takes.</summary>
    private Task<Permission> First(string path, string user)
        => Settle(path, user, _ => true, "the fold must answer");

    private static bool IsEditor(Permission p)
        => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Update) && p.HasFlag(Permission.Create);

    [Fact]
    public async Task AGroupMember_HoldsTheRuleRole_OnAMatchingRootAndItsDescendants()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);

        IsEditor(await Settle(client, Alice, IsEditor, "the rule grants the group Editor at the root"))
            .Should().BeTrue();
        var deep = await First($"{client}/Deal/Offer", Alice);
        IsEditor(deep).Should().BeTrue("a role at the partition root inherits to every descendant");
    }

    [Fact]
    public async Task ANestedGroupMember_HoldsTheRuleRole()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);

        IsEditor(await Settle($"{client}/Deal", Nora, IsEditor,
                "Nora is in Sales/Leads, which is in Sales/Members — group expansion is transitive"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task AnAdminRule_GrantsAdmin()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRule("sales-leads-admin", SalesLeads, Role.Admin.Id);

        var p = await Settle(client, Nora, x => x.HasFlag(Permission.Delete) && x.HasFlag(Permission.Update),
            "the Admin rule grants Sales/Leads Admin at the root");
        p.HasFlag(Permission.Read).Should().BeTrue();
    }

    [Fact]
    public async Task ARootWhoseContentDoesNotMatch_GetsNothing()
    {
        var client = Unique("RuleClient");
        var partner = Unique("RulePartner");
        await WriteRoot(client, ClientType);
        await WriteRoot(partner, PartnerType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);

        // Control: the same rule, the same user, the same mesh — a matching root IS granted.
        await Settle(client, Alice, IsEditor, "control: the rule applies to the client sibling");

        (await First(partner, Alice)).Should().Be(Permission.None,
            "a Partner root does not carry content.type == Client, so the rule does not apply");
        (await First($"{partner}/Deal", Alice)).Should().Be(Permission.None);
    }

    [Fact]
    public async Task ARootOfAnotherNodeType_GetsNothing()
    {
        var client = Unique("RuleClient");
        var space = Unique("RuleSpace");
        await WriteRoot(client, ClientType);
        await WriteRoot(space, ClientType, nodeType: "Markdown");
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);

        await Settle(client, Alice, IsEditor, "control: the rule applies to the Counterparty sibling");
        (await First(space, Alice)).Should().Be(Permission.None,
            "the rule names the node type; a root of another type with the same content is not covered");
    }

    [Fact]
    public async Task Retyping_TheRootAwayFromTheMatch_RemovesTheGrant_AndBackRestoresIt()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);
        await Settle($"{client}/Deal", Alice, IsEditor, "granted while the root is a Client");

        await WriteRoot(client, PartnerType);
        await Settle($"{client}/Deal", Alice, p => p == Permission.None,
            "the root is no longer a Client — nothing was materialised, so nothing lingers");

        await WriteRoot(client, ClientType);
        await Settle($"{client}/Deal", Alice, IsEditor, "retyped back to Client, the rule applies again");
    }

    [Fact]
    public async Task ARuleDeclaredLater_CoversAnExistingPartition_AndDeletingItRemovesTheGrant()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        (await First(client, Alice)).Should().Be(Permission.None, "no rule yet");

        await WriteRule("late", SalesMembers, Role.Editor.Id);
        await Settle(client, Alice, IsEditor, "the rule covers the partition from the moment it exists");

        await Storage.Delete(GrantRule.PathOf("late"))
            .Should().Within(Budget).Emit("the rule must be deleted", cancellationToken: TestContext.Current.CancellationToken);
        await Settle(client, Alice, p => p == Permission.None, "deleting the rule removes the grant");
    }

    [Fact]
    public async Task ADeniedAssignment_AtTheClient_WinsOverTheRule()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRoot(DeniedClient, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);

        // Control: the rule applies to Alice on an ordinary client, and to Nora on the denied one.
        await Settle(client, Alice, IsEditor, "control: Alice holds the rule role elsewhere");
        await Settle(DeniedClient, Nora, IsEditor, "control: the rule applies to the denied client");

        (await First(DeniedClient, Alice)).Should().Be(Permission.None,
            "an AccessAssignment denying Editor at the partition subtracts the rule-derived Editor");
        (await First($"{DeniedClient}/Deal", Alice)).Should().Be(Permission.None);
    }

    [Fact]
    public async Task AnOutsider_GetsNothing()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);

        await Settle(client, Alice, IsEditor, "control: the member is granted");
        (await First(client, Olaf)).Should().Be(Permission.None, "Olaf is in no group the rule names");
    }

    [Fact]
    public async Task ABroadSubject_IsNeverHonoured()
    {
        var client = Unique("RuleClient");
        await WriteRoot(client, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);
        await WriteRule("everyone", WellKnownUsers.Public, Role.Viewer.Id);

        await Settle(client, Alice, IsEditor, "control: the named-subject rule applies");
        (await First(client, Olaf)).Should().Be(Permission.None,
            "a rule naming Public would open every matching partition to every signed-in user");
    }

    [Fact]
    public async Task OnASystemOwnedPartition_TheRuleConfersReadButNoWrite()
    {
        var client = Unique("RuleClient");
        var owned = Unique("RuleOwned");
        await WriteRoot(client, ClientType);
        await WriteRoot(owned, ClientType);
        await WriteRule("sales-members-editor", SalesMembers, Role.Editor.Id);
        await Settle(client, Alice, IsEditor, "control: the rule confers write on an ordinary client");

        var config = await Sync.SaveConfig(owned, RepoUrl, "main", null, false, false,
                direction: SyncDirection.ImportOnly, twoWay: false)
            .Should().Within(Budget).Emit("the sync config must be written",
                cancellationToken: TestContext.Current.CancellationToken);
        AccessAssignmentGuard.IsSystemOwned(config, Mesh.JsonSerializerOptions).Should().BeTrue();

        var p = await Settle($"{owned}/Deal", Alice, x => x.HasFlag(Permission.Read)
                && (x & (Permission.Create | Permission.Update | Permission.Delete)) == Permission.None,
            "a one-way synced partition's content is written by its importer alone — no role, a "
            + "rule-derived one included, confers Create/Update/Delete there (#5140)");
        p.HasFlag(Permission.Read).Should().BeTrue();
    }
}

/// <summary>The content of a test partition root — the field the rule filters on is <c>type</c>.</summary>
public record RuleTestCounterparty
{
    /// <summary>The counterparty kind, a dimension-member path.</summary>
    public string Type { get; init; } = "";

    /// <summary>The owner.</summary>
    public string? Owner { get; init; }
}

/// <summary>The pure halves: reading and matching rules, and the broad-grant guard's view of one.</summary>
public class GrantRuleEvaluatorTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static MeshNode Root(string path, string nodeType, object content)
        => new(path) { NodeType = nodeType, Content = JsonSerializer.SerializeToElement(content, Options) };

    private static readonly GrantRule ClientEditor = new()
    {
        NodeType = "Crm/Counterparty", ContentField = "type", ContentValue = "Crm/CounterpartyType/Client",
        AccessObject = "Sales/Members", Role = "Editor",
    };

    [Fact]
    public void Matching_HonoursTypeAndContentField()
    {
        var rules = ImmutableArray.Create(ClientEditor);
        GrantRuleEvaluator.Matching(rules, Root("Acme", "Crm/Counterparty", new { type = "Crm/CounterpartyType/Client" }), Options)
            .Should().ContainSingle();
        GrantRuleEvaluator.Matching(rules, Root("Acme", "Crm/Counterparty", new { type = "Crm/CounterpartyType/Partner" }), Options)
            .Should().BeEmpty();
        GrantRuleEvaluator.Matching(rules, Root("Acme", "Crm/Counterparty", new { owner = "x" }), Options)
            .Should().BeEmpty("a missing field never matches");
        GrantRuleEvaluator.Matching(rules, Root("Acme/Deal", "Crm/Counterparty", new { type = "Crm/CounterpartyType/Client" }), Options)
            .Should().BeEmpty("only a partition ROOT is matched");
        GrantRuleEvaluator.Matching(rules, null, Options).Should().BeEmpty("a partition without a root matches nothing");
    }

    [Fact]
    public void ReadRules_DropsIneffectiveAndMisplacedRules()
    {
        var nodes = new[]
        {
            new MeshNode("ok", GrantRule.Namespace) { NodeType = GrantRule.NodeTypeName, Content = ClientEditor },
            new MeshNode("public", GrantRule.Namespace) { NodeType = GrantRule.NodeTypeName, Content = ClientEditor with { AccessObject = "Public" } },
            new MeshNode("norole", GrantRule.Namespace) { NodeType = GrantRule.NodeTypeName, Content = ClientEditor with { Role = "" } },
            new MeshNode("elsewhere", "Acme/_GrantRule") { NodeType = GrantRule.NodeTypeName, Content = ClientEditor },
        };
        GrantRuleEvaluator.ReadRules(nodes, Options).Should().ContainSingle();
    }

    [Fact]
    public void ARuleWrittenAsJsonObject_IsReadAndNamesItsSubject()
    {
        // The governance executor writes a rule's content as a JsonObject — the shape must read.
        var content = new System.Text.Json.Nodes.JsonObject
        {
            ["$type"] = "GrantRule", ["nodeType"] = "Crm/Counterparty", ["contentField"] = "type",
            ["contentValue"] = "Crm/CounterpartyType/Client", ["accessObject"] = "Sales/Members", ["role"] = "Editor",
        };
        var node = new MeshNode("r", GrantRule.Namespace) { NodeType = GrantRule.NodeTypeName, Content = content };
        GrantRuleEvaluator.ReadRules([node], Options).Should().ContainSingle();
        BroadGrantGuard.EvaluateWrite(node, assignment: null, new AccessContext { ObjectId = "a-global-admin" }, Options)!
            .Subject.Should().Be("Sales/Members", "the refusal and the audit line name who the rule grants");
    }

    [Fact]
    public void AGrantRuleWrite_IsABroadGrant_AndItsStandardIsAllowlisted()
    {
        var node = new MeshNode("r", GrantRule.Namespace) { NodeType = GrantRule.NodeTypeName, Content = ClientEditor };
        var finding = BroadGrantGuard.Evaluate(node, assignment: null,
            new AccessContext { ObjectId = "a-global-admin" });
        finding.Should().NotBeNull("one rule grants on every matching partition, present and future");
        finding!.Kind.Should().Be(BroadGrantKind.GrantRule);
        finding.Subject.Should().Be("Sales/Members");
        BroadGrantGuard.DefaultGovernedStandards.Should().Contain("access.grant-rule");
    }
}
