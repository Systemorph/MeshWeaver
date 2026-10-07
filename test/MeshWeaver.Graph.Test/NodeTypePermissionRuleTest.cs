using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A node type's DECLARED access rule must hold at every seam that asks — including the two that
/// did not consult it: the <c>[RequiresPermission]</c> delivery gate for a permission OUTSIDE the CRUD
/// four (<see cref="Permission.Thread"/>, <see cref="Permission.Comment"/>, …), and the
/// <c>MeshNodeStreamCache</c> read gate every <c>GetMeshNodeStream</c> view goes through.
///
/// <para>The trigger (maintainer, 2026-10-04): a platform admin must be able to observe and steer the
/// conversations SYSTEM agents run, through a rule the conversation's node type declares — never a
/// hand-written grant and never an implicit superuser read. Writing into a conversation creates its
/// message cells, which the delivery gate demands <see cref="Permission.Thread"/> for, and
/// <see cref="NodeTypeAccessRuleGate.SubjectOperationFor"/> maps that permission to no operation — so no
/// rule could ever speak for it. And a rule's Read grant held for queries and subscriptions while the
/// stream cache — the read every node view does — still answered "lacks Read permission".</para>
///
/// <para>Pinned with a test-local rule on <c>Markdown</c> (the ordinary fold OR one named guest), on a
/// mesh WITHOUT the blanket public-admin grant. Each case has its outsider control, so a fix that simply
/// widened the gate fails half of them.</para>
/// </summary>
public class NodeTypePermissionRuleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "PermRuleSpace";
    private const string Conversation = Partition + "/Conv";
    private const string Guest = "rule-guest";
    private const string Outsider = "rule-outsider";

    private static TimeSpan Budget => TestTimeouts.Quick;

    // 🚨 ConfigureMeshBase, never base.ConfigureMesh — the latter grants Public the Admin role and every
    // assertion below would pass vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = "Permission rule space", NodeType = "Markdown" },
                new MeshNode("Conv", Partition) { Name = "Conversation", NodeType = "Markdown" })
            .ConfigureServices(services => services.AddSingleton<INodeTypeAccessRule>(sp =>
                new GuestRule(sp.GetRequiredService<IMessageHub>())));

    /// <summary>
    /// The test-local rule: the ordinary fold first (so nothing changes for anybody else), then the
    /// named guest — Read through <see cref="INodeTypeAccessRule.HasAccess"/>, Thread through
    /// <see cref="INodeTypePermissionRule.HasPermission"/>. Nothing else.
    /// </summary>
    private sealed class GuestRule(IMessageHub hub) : INodeTypePermissionRule
    {
        public string NodeType => "Markdown";
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IReadOnlyCollection<Permission> SupportedPermissions => [Permission.Thread];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
            => userId == Guest
                ? Observable.Return(true)
                : hub.CheckPermission(context.Node.Path, userId ?? WellKnownUsers.Anonymous, Permission.Read).Take(1);

        public IObservable<bool> HasPermission(MeshNode node, AccessContext? accessContext, string? userId, Permission permission)
            => Observable.Return(userId == Guest && permission == Permission.Thread);
    }

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    /// <summary>
    /// A message cell posted into the conversation exactly as a round posts one: a
    /// <see cref="CreateNodeRequest"/> at the conversation's OWN address, whose delivery gate demands
    /// <see cref="Permission.Thread"/> on that address (<c>CreateNodePermissionAttribute</c>). Yields the
    /// gate's refusal message, or null when the gate let the delivery through (whatever the handler then
    /// decided about the cell is a different seam's answer).
    /// </summary>
    private IObservable<string?> GateRefusal(string userId)
        => RequestHub
            .Observe(new CreateNodeRequest(new MeshNode("m1", Conversation) { NodeType = "ThreadMessage" }),
                o => o.WithTarget(new Address(Conversation)).WithAccessContext(Identity(userId)))
            .Take(1)
            .Select(_ => (string?)null)
            .Catch((DeliveryFailureException ex) => Observable.Return<string?>(
                ex.Failure.Message is { } m && m.Contains("lacks Thread permission", StringComparison.Ordinal) ? m : null));

    /// <summary>
    /// 🚨 THE PIN for the delivery gate. Pre-change the guest was refused "lacks Thread permission on
    /// 'PermRuleSpace/Conv'": the gate mapped Thread to no operation and never asked the node type.
    /// </summary>
    [Fact]
    public async Task ADeclaredThreadRule_IsHonouredByTheDeliveryGate()
    {
        var refusal = await GateRefusal(Guest).Should().Within(Budget).Emit();
        refusal.Should().BeNull("the node type's INodeTypePermissionRule grants Thread to the guest: " + refusal);
    }

    /// <summary>The control: a user the rule does not name is still refused by the gate.</summary>
    [Fact]
    public async Task AnUndeclaredUser_IsStillRefusedThread()
    {
        var refusal = await GateRefusal(Outsider).Should().Within(Budget).Emit();
        refusal.Should().NotBeNull("the rule grants Thread to the guest only; consulting it must never mean granting");
    }

    private IObservable<string> CacheRead(string userId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(Identity(userId));
        access.SetHostIdentity(Identity(userId));
        return Mesh.GetWorkspace().GetMeshNodeStream(Conversation)
            .Where(n => n is not null)
            .Take(1)
            .Select(n => "read:" + n!.Path)
            .Catch((UnauthorizedAccessException ex) => Observable.Return("denied:" + ex.Message));
    }

    /// <summary>
    /// 🚨 THE PIN for the stream-cache read gate. Pre-change the guest was refused "lacks Read
    /// permission on 'PermRuleSpace/Conv'" here while the same rule granted the read to a query.
    /// </summary>
    [Fact]
    public async Task ADeclaredReadRule_IsHonouredByTheStreamCache()
    {
        var outcome = await CacheRead(Guest).Should().Within(Budget).Emit();
        outcome.Should().Be("read:" + Conversation);
    }

    /// <summary>The control: the cache still refuses a user neither the fold nor the rule admits.</summary>
    [Fact]
    public async Task AnUndeclaredUser_IsStillRefusedTheRead()
    {
        var outcome = await CacheRead(Outsider).Should().Within(Budget).Emit();
        outcome.Should().StartWith("denied:");
    }
}
