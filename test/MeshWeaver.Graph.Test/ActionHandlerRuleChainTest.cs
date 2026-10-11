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

    private static string NewKey() => "test.action-" + Guid.NewGuid().ToString("N")[..8];

    private static ActionInvocation Invocation(string key, int version = 1) =>
        new() { HandlerKey = key, HandlerVersion = version };

    private async Task<string> WriteRule(ActionHandlerRule rule) =>
        await WriteRuleNode(rule.Id, rule);

    private async Task<string> WriteRuleNode(string id, object content)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, $"{Partition}/ActionHandlers")
        {
            Name = "action handler rule probe",
            NodeType = ActionHandlerRuleNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = content,
        }).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        return $"{Partition}/ActionHandlers/{id}";
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
