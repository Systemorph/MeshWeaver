using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Issue #2913 — <b>the delete pipeline used to answer the permission question TWICE, and the two
/// answers disagreed.</b> <c>RlsNodeValidator</c> routed Delete through the node type's registered
/// <see cref="INodeTypeAccessRule"/>; the delete handler's pre-flight
/// (<c>MeshExtensions.CheckDeletePermissionForNode</c>) demanded <see cref="Permission.Delete"/>
/// outright and never looked at a rule. <c>SatelliteAccessRule</c> maps a satellite's Delete to
/// <see cref="Permission.Update"/> on its <c>MainNode</c>, and <c>Role.Editor</c> holds Update and
/// NOT Delete — so an Editor could PUBLISH a satellite onto a node and then be refused when erasing
/// it. "I can turn it on but not off" is the exact state a revocable-consent feature exists to
/// prevent, and it was reachable by an ordinary Editor.
///
/// <para>These four tests pin the whole decision, not just the happy half — the fix is only correct
/// if it widened EXACTLY the rule-governed case:</para>
/// <list type="number">
/// <item>An Editor CAN now delete a satellite it may publish (the issue's case).</item>
/// <item>The same Editor still CANNOT delete a node whose type has NO rule — the default is still
/// <see cref="Permission.Delete"/>, closed by default. <b>This is the test that matters most.</b></item>
/// <item>A rule that FAULTS denies (<see cref="NodeDeletionRejectionReason.Unavailable"/>), never
/// falls through to allow — the fail-open shape that bit #2011.</item>
/// <item>A rule that says NO denies even a caller who holds <see cref="Permission.Delete"/>: the
/// rule is the decision, not a second opinion that a raw permission can outvote.</item>
/// </list>
///
/// <para>The delete is issued at <c>NodeOperationTarget()</c> — the mesh's off-router node-operation
/// execution hub, which is where <c>IMeshService.DeleteNode</c> lands and which carries NO
/// per-node <c>AccessControlPipeline</c>. That is deliberate: on THAT route the handler's pre-flight
/// IS the gate, so these tests exercise the code the fix changed rather than a delivery gate in
/// front of it.</para>
/// </summary>
public class DeleteHonoursNodeTypeAccessRuleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Editor = "editor-2913";
    private const string Space = TestPartition + "/space-2913";
    private const string MainNode = Space + "/doc";

    /// <summary>A satellite type whose access rule delegates every write to its MainNode.</summary>
    private const string SatelliteType = "SatelliteUnderTest2913";

    /// <summary>A type whose rule cannot answer — it faults.</summary>
    private const string FaultingType = "FaultingRuleType2913";

    /// <summary>A type whose rule answers NO, unconditionally.</summary>
    private const string DenyingType = "DenyingRuleType2913";

    /// <summary>
    /// The xunit wedge backstop. It must DOMINATE the inner waits below, which are
    /// <see cref="TestTimeouts.Convergence"/> — 36 s locally and 108 s on CI (the 3× CI factor).
    /// Written as a constant only because an attribute argument has to be one; the value is
    /// <c>TestTimeouts.Convergence × 2</c> at the CI factor, i.e. the same margin
    /// <c>TestTimeouts.TestMilliseconds</c> applies. A test whose outer bound does not dominate its
    /// inner wait can only ever fail anonymously (#2819).
    /// </summary>
    private const int FactTimeoutMs = 240_000;

    /// <summary>
    /// Stand-in for whatever a real rule's exception might carry — an internal path, a connection
    /// string, another tenant's identifier. The caller-visible refusal must name the exception TYPE
    /// and NOT this text; the full exception belongs in the operator's log only.
    /// </summary>
    private const string SecretInFaultMessage = "internal-detail-that-must-not-reach-the-caller";

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>
    /// No <c>PublicAdminAccess</c> — a security test must be able to observe a denial. The DevLogin
    /// admin keeps the static root Admin grant <c>ConfigureMeshBase</c> installs, so the fixtures
    /// below are created by an authorised identity.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                PlainType(SatelliteType, "Satellite Under Test"),
                PlainType(FaultingType, "Faulting Rule Type"),
                PlainType(DenyingType, "Denying Rule Type"))
            .ConfigureServices(services =>
            {
                // The REAL SatelliteAccessRule — the production rule the issue is about, not a
                // stand-in: Delete on a satellite requires Update on its MainNode.
                services.AddSingleton<INodeTypeAccessRule>(sp =>
                    new SatelliteAccessRule(SatelliteType, sp.GetRequiredService<IMessageHub>()));
                services.AddSingleton<INodeTypeAccessRule>(new ScriptedAccessRule(
                    FaultingType,
                    () => Observable.Throw<bool>(new InvalidOperationException(SecretInFaultMessage))));
                services.AddSingleton<INodeTypeAccessRule>(new ScriptedAccessRule(
                    DenyingType, () => Observable.Return(false)));
                return services;
            });

    /// <summary>A minimal NodeType definition — enough for the create handler's type-exists gate.</summary>
    private static MeshNode PlainType(string nodeType, string name) => new(nodeType)
    {
        Name = name,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<AccessObject>())
    };

    /// <summary>An <see cref="INodeTypeAccessRule"/> whose Delete answer is supplied by the test.</summary>
    private sealed class ScriptedAccessRule(string nodeType, Func<IObservable<bool>> answer) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => answer();
    }

    private async Task SeedFixtureAsync()
    {
        await NodeFactory.CreateNode(new MeshNode("space-2913", TestPartition)
        { Name = "Space 2913", NodeType = "Group" }).Should().Within(TestTimeouts.Convergence).Emit();
        await NodeFactory.CreateNode(new MeshNode("doc", Space)
        { Name = "Doc", NodeType = "Markdown" }).Should().Within(TestTimeouts.Convergence).Emit();

        // The Editor's ONLY grant: Editor on the space. Read | Create | Update | Comment | … and
        // deliberately NOT Delete — see Role.Editor.
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Editor, "Editor", Space))
            .Should().Within(TestTimeouts.Convergence).Emit();

        // Wait until the live permission fold — the same fold every gate reads — sees the grant,
        // so the delete below races nothing. Assert BOTH halves: the Editor holds Update (which is
        // what the satellite rule delegates to) and does NOT hold Delete (which is what the
        // pre-flight used to demand). Without the second half this fixture could silently drift
        // into granting Delete and every assertion below would pass for the wrong reason.
        await Mesh.GetEffectivePermissions(MainNode, Editor)
            .Should().Within(TestTimeouts.Convergence)
            .Match(p => p.HasFlag(Permission.Update) && !p.HasFlag(Permission.Delete));
    }

    private Task<DeleteNodeResponse> DeleteAs(string path, AccessContext identity) =>
        RequestHub
            .Observe(new DeleteNodeRequest(path) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(RequestHub.NodeOperationTarget()).WithAccessContext(identity))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit();

    private static AccessContext As(string userId) => new() { ObjectId = userId, Name = userId };

    // ─── 1. The issue: an Editor may PUBLISH a satellite, so it may ERASE it ───

    [Fact(Timeout = FactTimeoutMs)]
    public async Task Editor_CanDelete_ASatelliteItMayPublish()
    {
        await SeedFixtureAsync();

        // A satellite of the doc: its MainNode is the doc, exactly as a presence / activity /
        // thread row written onto a node is. Creating it requires Update on the MainNode — which
        // the Editor holds — so removing it must too.
        var satellite = $"{MainNode}/presence";
        await NodeFactory.CreateNode(new MeshNode("presence", MainNode)
        { Name = "Presence", NodeType = SatelliteType, MainNode = MainNode })
            .Should().Within(TestTimeouts.Convergence).Emit();

        var response = await DeleteAs(satellite, As(Editor));

        response.Success.Should().BeTrue(
            "SatelliteAccessRule maps a satellite's Delete to Update on its MainNode and the Editor "
            + "holds Update there, so the delete pre-flight must reach the SAME verdict the RLS "
            + $"validator does (#2913) — got: {response.Error}");

        // AUTHORITATIVE: storage, below the security layer.
        (await Storage.Read(satellite, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence).Emit())
            .Should().BeNull("the satellite must actually be gone, not merely reported as deleted");
    }

    // ─── 2. 🚨 THE ONE THAT MATTERS: nothing widened for a type with NO rule ───

    [Fact(Timeout = FactTimeoutMs)]
    public async Task Editor_StillCannotDelete_APlainNodeWithNoAccessRule()
    {
        await SeedFixtureAsync();

        // Same partition, same Editor, same Update-without-Delete grant — the ONLY difference from
        // the test above is that "Markdown" has no INodeTypeAccessRule. The fallback must still be
        // Permission.Delete, i.e. closed by default.
        var plain = $"{Space}/plain";
        await NodeFactory.CreateNode(new MeshNode("plain", Space)
        { Name = "Plain", NodeType = "Markdown" }).Should().Within(TestTimeouts.Convergence).Emit();

        var response = await DeleteAs(plain, As(Editor));

        response.Success.Should().BeFalse(
            "no rule governs 'Markdown', so the delete must still demand Permission.Delete — "
            + "routing the pre-flight through the rule chain must not widen anything else");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Unauthorized,
            $"this is a decision about the caller's rights, not an availability failure: {response.Error}");

        (await Storage.Read(plain, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence).Emit())
            .Should().NotBeNull("a denied delete must remove nothing");
    }

    // ─── 3. A rule that faults DENIES — it never falls through to allow ───

    [Fact(Timeout = FactTimeoutMs)]
    public async Task AFaultingRule_RefusesTheDelete_AndReportsUnavailableNotDenied()
    {
        await SeedFixtureAsync();

        var faulty = $"{Space}/faulty";
        await NodeFactory.CreateNode(new MeshNode("faulty", Space)
        { Name = "Faulty", NodeType = FaultingType, MainNode = Space })
            .Should().Within(TestTimeouts.Convergence).Emit();

        // The DevLogin admin — who holds Permission.Delete outright — so a pass here could only
        // mean the rule was bypassed rather than that the caller was entitled.
        var response = await DeleteAs(faulty, TestUsers.Admin);

        response.Success.Should().BeFalse(
            "a registered rule that cannot answer must FAIL CLOSED — an exception on the way to a "
            + "verdict is not permission to proceed (the fail-open shape of #2011)");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Unavailable,
            "no verdict was reached, so the honest report is an availability failure — collapsing it "
            + "into Unauthorized would send a correctly-entitled caller to request rights they hold");
        response.Error.Should().Contain("could not be established",
            $"the refusal must say WHICH failure it was: {response.Error}");
        response.Error.Should().Contain(nameof(InvalidOperationException),
            "the exception TYPE names the condition and is what a caller can act on");
        response.Error.Should().NotContain(SecretInFaultMessage,
            "a rule's raw exception MESSAGE must never be echoed to the caller — it can carry "
            + "internal paths or another tenant's identifiers; it belongs in the operator's log "
            + "only (Copilot review, #2945)");

        (await Storage.Read(faulty, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence).Emit())
            .Should().NotBeNull("a delete that could not be authorised must remove nothing");
    }

    // ─── 4. A rule that says NO outranks a raw Permission.Delete ───

    [Fact(Timeout = FactTimeoutMs)]
    public async Task ADenyingRule_RefusesTheDelete_EvenForACallerHoldingDelete()
    {
        await SeedFixtureAsync();

        var denied = $"{Space}/denied";
        await NodeFactory.CreateNode(new MeshNode("denied", Space)
        { Name = "Denied", NodeType = DenyingType, MainNode = Space })
            .Should().Within(TestTimeouts.Convergence).Emit();

        var response = await DeleteAs(denied, TestUsers.Admin);

        response.Success.Should().BeFalse(
            "the node type's rule IS the decision — it must not be outvoted by the caller happening "
            + "to hold Permission.Delete, which is the direction the old pre-flight could widen in");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Unauthorized,
            $"the rule reached a verdict and it was NO: {response.Error}");

        (await Storage.Read(denied, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence).Emit())
            .Should().NotBeNull("a rule-denied delete must remove nothing");
    }
}
