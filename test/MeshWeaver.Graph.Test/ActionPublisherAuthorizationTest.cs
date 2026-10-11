#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.ActionLinks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#4692 — publisher authorization on a LIVE chain fails closed and runs through the
/// mesh's one permission seam (<c>hub.CheckPermissionOutcome</c>), as the publisher.
///
/// <para>🚨 Built on <c>ConfigureMeshBase</c>, never <c>base.ConfigureMesh</c>: the latter grants
/// Public the Admin role at the root, so every publisher would hold every permission and the refusal
/// below would be unobservable. Only the DevLogin admin (the harness's setup identity) and one
/// named editor in the test partition hold anything.</para>
/// </summary>
public class ActionPublisherAuthorizationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "PubAuthSpace";
    private const string Editor = "publisher-editor";
    private const string Outsider = "publisher-outsider";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder)
            .AddMeshNodes(TestUsers.DevLoginAdminAccess())
            .AddMeshNodes(
                new MeshNode(Partition) { Name = "Publisher authorization space", NodeType = "Markdown" },
                new MeshNode($"{Editor}_Access", $"{Partition}/_Access")
                {
                    NodeType = "AccessAssignment",
                    Name = "Editor",
                    MainNode = Partition,
                    Content = new AccessAssignment
                    {
                        AccessObject = Editor,
                        DisplayName = Editor,
                        Roles = [new RoleAssignment { Role = "Editor" }],
                    },
                })
            .DeclareActionHandlerRuleNamespaces(Partition);

    private static string NewKey() => "test.publish-" + Guid.NewGuid().ToString("N")[..8];

    private static ActionPublicationContext Publication(string publisher) => new()
    {
        Publisher = publisher,
        Site = new ActionSite { SiteId = "portal", Origin = "https://portal.example.com" },
        PublisherRequestId = "r-" + Guid.NewGuid().ToString("N")[..6],
    };

    private async Task<IActionHandler> BoundHandler(string key, string? publishPermission)
    {
        var id = "rule" + Guid.NewGuid().ToString("N")[..6];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, $"{Partition}/ActionHandlers")
        {
            Name = "publisher authorization probe",
            NodeType = ActionHandlerRuleNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new ActionHandlerRule
            {
                Id = id,
                Claims = [key],
                Handler = $"{Partition}/Review",
                PublishPermission = publishPermission,
            },
        }).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        // Wait on the condition — the live chain binds the rule — never a delay.
        await Mesh.ActionHandlerChain()
            .Where(chain => chain.Handlers.Any(h => h.Capability.HandlerKey == key))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        var resolution = await Mesh.ResolveActionHandler(new ActionInvocation { HandlerKey = key, HandlerVersion = 1 })
            .Timeout(TestTimeouts.Convergence).Await();
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        return resolution.Handler!;
    }

    private static Task<ActionOutcome> Validate(IActionHandler handler, string key, string publisher) =>
        handler.Validate(new ActionInvocation { HandlerKey = key, HandlerVersion = 1 }, Publication(publisher))
            .Timeout(TestTimeouts.Convergence).Await();

    [Fact]
    public async Task APublisherWithoutTheStatedPermission_IsRefused_ThroughTheMeshPermissionSeam()
    {
        var key = NewKey();
        var handler = await BoundHandler(key, nameof(Permission.Update));

        var refused = await Validate(handler, key, Outsider);
        refused.Status.Should().Be(ActionOutcomeStatus.Unauthorized,
            "the outsider holds no grant in the partition, so the mesh's permission fold denies Update");
        refused.Detail.Should().Contain("lacks Update").And.Contain($"{Partition}/Review");

        var admitted = await Validate(handler, key, Editor);
        admitted.Status.Should().Be(ActionOutcomeStatus.Ready,
            "the control: the editor holds Update on the handler node through a real AccessAssignment");
    }

    [Fact]
    public async Task ALiveRuleThatStatesNoPublishPermission_RefusesEvenAPublisherWithEveryGrant()
    {
        var key = NewKey();
        var handler = await BoundHandler(key, publishPermission: null);

        var outcome = await Validate(handler, key, TestUsers.Admin.ObjectId!);
        outcome.Status.Should().Be(ActionOutcomeStatus.Unauthorized,
            "no default path authorizes a publisher: the rule must state who may publish");
    }

    [Fact]
    public void TheConfiguredRuleNamespaces_FollowAConfigurationReload()
    {
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ActionLinks:RuleNamespaces:0"] = "Early" })
            .Build();
        var source = new PlatformActionHandlerRuleNamespaces(
            [new ActionHandlerRuleNamespaceDeclaration(["Admin"])], root);

        var seen = ImmutableList<ImmutableArray<string>>.Empty;
        using (source.Namespaces(Mesh).Subscribe(set => seen = seen.Add([.. set])))
        {
            seen.Should().ContainSingle();
            seen[0].Should().Equal("Admin", "Early");

            root["ActionLinks:RuleNamespaces:1"] = "Late";
            root.Reload();

            seen.Should().HaveCount(2, "a reload pushes a new declaration to a subscription that is already live");
            seen[1].Should().Equal("Admin", "Early", "Late");
        }

        root["ActionLinks:RuleNamespaces:2"] = "AfterDispose";
        root.Reload();
        seen.Should().HaveCount(2, "a disposed subscription drops its reload registration");
    }
}
