#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ActionLinks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#4692, shape 3 — the action-handler chain is a set of RULE NODES in the mesh, read
/// live, never a service a compiled module registers. A package the mesh compiles (a store) cannot
/// put a service in the root container; it can write a node, and this is the seam that lets it.
///
/// <para><b>What is observed.</b> A real monolith mesh. Rule nodes are created through the mesh's
/// own create API and the chain is resolved through <c>hub.ResolveActionHandler</c>, which reads
/// the synced query the portal reads. Every test claims its own key, so the mesh-wide chain other
/// tests also write to cannot decide its verdict.</para>
/// </summary>
public class ActionHandlerRuleChainTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "TestData";

    /// <summary>A partition the test may write into but that is NOT declared for rules.</summary>
    private const string Undeclared = "UndeclaredRules";

    /// <summary>Declares the test's own partition for rules — the platform's declaration seam, used as a deployment would.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).DeclareActionHandlerRuleNamespaces(Partition);

    private static string NewKey() => "test.action-" + Guid.NewGuid().ToString("N")[..8];

    private static ActionInvocation Invocation(string key, int version = 1) =>
        new() { HandlerKey = key, HandlerVersion = version };

    private async Task<string> WriteRule(ActionHandlerRule rule, string partition = Partition) =>
        await WriteRuleNode(rule.Id, rule, partition);

    private async Task<string> WriteRuleNode(string id, object content, string partition = Partition)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, $"{partition}/ActionHandlers")
        {
            Name = "action handler rule probe",
            NodeType = ActionHandlerRuleNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = content,
        }).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        return $"{partition}/ActionHandlers/{id}";
    }

    private Task<ActionHandlerResolution> Resolve(ActionInvocation invocation) =>
        Mesh.ResolveActionHandler(invocation).Timeout(TestTimeouts.Convergence).Await();

    /// <summary>Waits on the live chain until it claims <paramref name="key"/> — the condition, never a delay.</summary>
    private Task<ActionHandlerChain> ChainClaiming(string key) =>
        Mesh.ActionHandlerChain()
            .Where(chain => chain.Handlers.Any(h => h.Capability.HandlerKey == key))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();

    [Fact]
    public async Task AnUnclaimedKey_ResolvesUnknownHandler_AndSaysHowManyRulesWereRead()
    {
        var resolution = await Resolve(Invocation(NewKey()));

        resolution.Handler.Should().BeNull();
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler);
        resolution.Outcome.Detail.Should().Contain("no enabled rule claims it").And.Contain("rule(s) read",
            "the terminal is loud: it reports the chain it walked, so 'nothing claims it' is a reading");
    }

    [Fact]
    public async Task ARuleNodeAddedAfterStart_IsPickedUpWithoutARecycle()
    {
        var key = NewKey();
        (await Resolve(Invocation(key))).Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler,
            "the chain has been read once already, before the rule exists");

        await WriteRule(new ActionHandlerRule
        {
            Id = "late" + Guid.NewGuid().ToString("N")[..6],
            Claims = [key],
            Handler = $"{Partition}/Coupons/Review",
        });
        await ChainClaiming(key);

        var resolution = await Resolve(Invocation(key));
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready,
            "a rule written while the portal runs takes effect on the next resolution — no recycle");
        resolution.Handler!.Capability.HandlerKey.Should().Be(key);
    }

    [Fact]
    public async Task ARuleInAnUndeclaredPartition_WithALowerOrder_DoesNotWin_AndIsReported()
    {
        var key = NewKey();
        var declared = await WriteRule(new ActionHandlerRule
        {
            Id = "declared" + Guid.NewGuid().ToString("N")[..6], Claims = [key], Order = 100,
            Handler = $"{Partition}/Coupons/Review",
        });
        var hijack = await WriteRule(new ActionHandlerRule
        {
            Id = "hijack" + Guid.NewGuid().ToString("N")[..6], Claims = [key], Order = 1,
            Handler = $"{Undeclared}/Phish",
        }, Undeclared);
        // Wait on the condition: both rules are in the snapshot — one bound, one refused by name.
        var chain = await Mesh.ActionHandlerChain()
            .Where(c => c.Handlers.Any(h => h.Capability.HandlerKey == key)
                        && c.Rejected.Any(r => r.Path == hijack))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        chain.Rejected.Should().Contain(new RejectedActionHandlerRule(hijack, ActionHandlerRules.UndeclaredNamespace));
        chain.DeclaredNamespaces.Should().Contain(Partition).And.NotContain(Undeclared);

        var resolution = await Resolve(Invocation(key));
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready,
            "the rule in the declared namespace still resolves");
        resolution.Handler!.Capability.Order.Should().Be(100,
            "Create rights in some partition are not a declaration — the lower-Order rule there does not win");
        resolution.Handler.ToString().Should().StartWith(declared);

        var onlyUndeclared = NewKey();
        var orphan = await WriteRule(new ActionHandlerRule
        {
            Id = "orphan" + Guid.NewGuid().ToString("N")[..6], Claims = [onlyUndeclared], Order = 1,
            Handler = $"{Undeclared}/Phish",
        }, Undeclared);
        await Mesh.ActionHandlerChain().Where(c => c.Rejected.Any(r => r.Path == orphan))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        var unknown = await Resolve(Invocation(onlyUndeclared));
        unknown.Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler);
        unknown.Outcome.Detail.Should().Contain($"{orphan} ({ActionHandlerRules.UndeclaredNamespace})",
            "an undeclared rule is named in the unclaimed detail, never dropped in silence");
    }

    [Fact]
    public async Task OrderDecides_BetweenTwoRulesClaimingOneKey_AndAnEqualOrderIsAmbiguous()
    {
        var key = NewKey();
        await WriteRule(new ActionHandlerRule
        {
            Id = "late" + Guid.NewGuid().ToString("N")[..6], Claims = [key], Order = 20, Handler = $"{Partition}/Second",
        });
        await WriteRule(new ActionHandlerRule
        {
            Id = "early" + Guid.NewGuid().ToString("N")[..6], Claims = [key], Order = 10, Handler = $"{Partition}/First",
        });
        await Mesh.ActionHandlerChain()
            .Where(chain => chain.Handlers.Count(h => h.Capability.HandlerKey == key) == 2)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        var chosen = await Resolve(Invocation(key));
        chosen.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        chosen.Handler!.Capability.Order.Should().Be(10, "the lower declared Order wins");

        var tied = NewKey();
        await WriteRule(new ActionHandlerRule
        {
            Id = "tieA" + Guid.NewGuid().ToString("N")[..6], Claims = [tied], Order = 5, Handler = $"{Partition}/A",
        });
        await WriteRule(new ActionHandlerRule
        {
            Id = "tieB" + Guid.NewGuid().ToString("N")[..6], Claims = [tied], Order = 5, Handler = $"{Partition}/B",
        });
        await Mesh.ActionHandlerChain()
            .Where(chain => chain.Handlers.Count(h => h.Capability.HandlerKey == tied) == 2)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        var ambiguous = await Resolve(Invocation(tied));
        ambiguous.Handler.Should().BeNull("neither is picked by the order the rules were written or read in");
        ambiguous.Outcome.Status.Should().Be(ActionOutcomeStatus.AmbiguousHandler);
    }

    [Fact]
    public async Task ARuleAuthoredAsMeshContent_NotACompiledService_Resolves_AndSendsTheRecipientToItsHandler()
    {
        // The case that decided the seam: the rule is DATA written into the mesh — the JSON a
        // package ships as a node file — with no compiled type and no service registration behind it.
        var key = NewKey();
        var id = "authored" + Guid.NewGuid().ToString("N")[..6];
        var authored = JsonSerializer.Deserialize<JsonElement>($$"""
            {
              "$type": "ActionHandlerRule",
              "id": "{{id}}",
              "claims": ["{{key}}"],
              "minVersion": 1,
              "maxVersion": 2,
              "order": 100,
              "handler": "{{Partition}}/Coupons/Review",
              "label": "Review the offer"
            }
            """);
        await WriteRuleNode(id, authored);
        await ChainClaiming(key);

        var resolution = await Resolve(Invocation(key, version: 2));
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);

        var context = new ActionRecipientContext
        {
            Recipient = "alice",
            Site = new ActionSite { SiteId = "portal", Origin = "https://portal.example.com" },
            LinkId = "6f1c2d3e",
        };
        var prepared = await resolution.Handler!.Prepare(Invocation(key, version: 2), context)
            .Timeout(TestTimeouts.Convergence).Await();

        prepared.Status.Should().Be(ActionOutcomeStatus.Ready);
        prepared.Navigation!.Target.Should().Be($"/{Partition}/Coupons/Review?link=6f1c2d3e");
        prepared.Navigation.Label.Should().Be("Review the offer");
    }
}
