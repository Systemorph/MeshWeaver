using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end tests for the release-creation workflow's clean user/System credential split:
///
/// <list type="number">
///   <item><b>Editor (has Compile) CAN create a release</b> via the gated entry point
///     <c>hub.RequestNodeTypeRelease</c>; the <c>Release</c> MeshNode lands and is stamped to the
///     editor (owner = caller), while the compile <c>_Activity</c> is stamped to System.</item>
///   <item><b>Viewer (no Compile) CANNOT</b> — the entry point refuses cleanly (onError, no
///     trigger flip, no new release).</item>
///   <item><b>A compile FAILURE leaves NO Release node</b> (atomic — never a partial release).</item>
///   <item><b>The cache-filling compilation runs as System</b> and succeeds on a read-only
///     partition where the triggering user has no Update right.</item>
/// </list>
///
/// RLS is on; <see cref="TestUsers.PublicAdminAccess"/> is deliberately NOT seeded (it would make
/// every user a global Admin), so the Editor/Viewer grants below are the only permissions in play.
/// NodeTypes are seeded under <see cref="AccessService.ImpersonateAsSystem"/> (infra bootstrap);
/// user actions run under an explicit <see cref="AccessService.SwitchAccessContext"/>.
/// </summary>
public class NodeTypeReleaseGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Two cold Roslyn compiles per editor test (kickoff + explicit release) need the larger budget.
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(120);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("rel-editor", "Editor", "RelGate"),
                AssignmentNodeFactory.UserRole("rel-viewer", "Viewer", "RelGate"),
                // Read-only partition (Doc-shaped): no Create/Update/Delete for ordinary users.
                AssignmentNodeFactory.Policy("RoPart",
                    new PartitionAccessPolicy { Create = false, Update = false, Delete = false }),
                AssignmentNodeFactory.UserRole("ro-user", "Viewer", "RoPart"));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IWorkspace Workspace => Mesh.GetWorkspace();

    private async Task SeedAsSystem(MeshNode node, CancellationToken ct)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(node).FirstAsync().ToTask(ct);
    }

    private static MeshNode NodeType(string id, string ns, string description) =>
        new(id, ns)
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = description,
                Configuration = "config => config.AddDefaultLayoutAreas()"
            }
        };

    [Fact(Timeout = 120_000)]
    public async Task Editor_WithCompile_CreatesRelease_StampedToUser_CompileActivityIsSystem()
    {
        var ct = new CancellationTokenSource(110.Seconds()).Token;
        const string typePath = "RelGate/Sample";

        await SeedAsSystem(NodeType("Sample", "RelGate", "Editor-creates-release test"), ct);

        // First-build kickoff (System) settles Ok with a kickoff release.
        var kickoff = await Workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));
        var kickoffReleasePath = ((NodeTypeDefinition)kickoff.Content!).LatestReleasePath!;

        // As the Editor (Editor role ⇒ Compile), request a release through the gated entry point.
        string? refusal = null;
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = "rel-editor", Name = "rel-editor" }))
            Mesh.RequestNodeTypeRelease(typePath, force: true, onError: e => refusal = e);
        refusal.Should().BeNull("an Editor holds Compile and must not be refused");

        // A NEW release lands (different from the kickoff), settled Ok.
        var settled = await Workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath)
                && d.LatestReleasePath != kickoffReleasePath);
        var newReleasePath = ((NodeTypeDefinition)settled.Content!).LatestReleasePath!;

        // The Release MeshNode exists and is OWNED BY THE EDITOR (owner = caller).
        var release = await Workspace.GetMeshNodeStream(newReleasePath)
            .Should().Within(15.Seconds())
            .Match(n => n is not null && n.Content is NodeTypeRelease);
        release.NodeType.Should().Be(ReleaseNodeType.NodeType);
        release.CreatedBy.Should().Be("rel-editor",
            "the release is the user-facing artefact — stamped to the user who requested it");

        // The compilation work (the compile _Activity) is stamped to SYSTEM, not the caller.
        var activityPath = ((NodeTypeDefinition)settled.Content!).LastCompilationActivityPath;
        activityPath.Should().NotBeNullOrEmpty();
        var activity = await Workspace.GetMeshNodeStream(activityPath!)
            .Should().Within(15.Seconds())
            .Match(n => n is not null && n.Content is ActivityLog);
        activity.CreatedBy.Should().Be(WellKnownUsers.System,
            "the pure compilation that fills the cache runs under System, never the caller");
    }

    [Fact(Timeout = 60_000)]
    public async Task Viewer_WithoutCompile_IsRefused_NoReleaseTriggered()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        const string typePath = "RelGate/ViewerSample";

        await SeedAsSystem(NodeType("ViewerSample", "RelGate", "Viewer-refused test"), ct);

        // Let the kickoff settle so the only way to a NEW release is an explicit (gated) request.
        await Workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);

        // As the Viewer (no Compile), request a release. The entry point must refuse cleanly.
        var refusals = new Subject<string>();
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = "rel-viewer", Name = "rel-viewer" }))
            Mesh.RequestNodeTypeRelease(typePath, force: true, onError: e => refusals.OnNext(e));

        var refusal = await refusals.FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        refusal.Should().Contain("Compile", "the refusal must explain the missing permission");

        // The gate returns BEFORE flipping the trigger — so RequestedReleaseAt must stay null.
        var node = await Workspace.GetMeshNodeStream(typePath).Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
        ((NodeTypeDefinition)node.Content!).RequestedReleaseAt.Should().BeNull(
            "a refused request must NOT flip the release trigger — no release work may start");
    }

    [Fact(Timeout = 120_000)]
    public async Task CompileFailure_LeavesNoReleaseNode()
    {
        var ct = new CancellationTokenSource(110.Seconds()).Token;
        const string typePath = "RelGate/Broken";

        // Seed a NodeType with a Source that does NOT compile.
        await SeedAsSystem(NodeType("Broken", "RelGate", "Atomic-on-failure test"), ct);
        await SeedAsSystem(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "public class Broken { this is not valid C# ;",
                Language = "csharp"
            }
        }, ct);

        // As the Editor, request a release. The Editor holds Compile, so the request is accepted —
        // but the compile FAILS.
        string? refusal = null;
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = "rel-editor", Name = "rel-editor" }))
            Mesh.RequestNodeTypeRelease(typePath, force: true, onError: e => refusal = e);
        refusal.Should().BeNull("the Editor is authorized — the failure is a COMPILE failure, not a refusal");

        // The NodeType settles to Error (never Ok), and carries no LatestReleasePath.
        var settled = await Workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompilationStatus == CompilationStatus.Error);
        ((NodeTypeDefinition)settled.Content!).LatestReleasePath.Should().BeNullOrEmpty(
            "a failed compile is atomic — it must NOT produce a Release");

        // No Release node exists under the NodeType — authoritative System query (RLS-blind).
        int releaseCount;
        using (Access.ImpersonateAsSystem())
            releaseCount = (await MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{typePath}/Release scope:subtree"))
                .FirstAsync().Timeout(15.Seconds()).ToTask(ct)).Items.Count;
        releaseCount.Should().Be(0,
            "a compile failure must leave NO Release MeshNode — never a partial release");
    }

    [Fact(Timeout = 120_000)]
    public async Task SystemCompile_FillsCache_OnReadOnlyPartition_WhereUserCannotWrite()
    {
        var ct = new CancellationTokenSource(110.Seconds()).Token;
        const string typePath = "RoPart/Sample";

        // The user has NO Update on the read-only partition...
        var userPerms = await Mesh.GetEffectivePermissions(typePath, "ro-user")
            .FirstAsync().ToTask(ct);
        userPerms.Should().NotHaveFlag(Permission.Update,
            "the read-only _Policy must deny Update to ordinary users — the premise of the test");

        // ...yet the System first-build kickoff fills the assembly cache AND creates the Release on
        // that very partition (System bypasses RLS; the activity/self-heal writes — fixed to run as
        // System — no longer fail closed). A NodeType seeded here settles Ok with a real release.
        await SeedAsSystem(NodeType("Sample", "RoPart", "System-fills-cache-on-read-only test"), ct);

        var settled = await Workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));
        var releasePath = ((NodeTypeDefinition)settled.Content!).LatestReleasePath!;

        // The Release node landed on the read-only partition — created by System.
        var release = await Workspace.GetMeshNodeStream(releasePath)
            .Should().Within(15.Seconds())
            .Match(n => n is not null && n.Content is NodeTypeRelease);
        release.CreatedBy.Should().Be(WellKnownUsers.System,
            "no user could write here — only the System-credentialed compile produced the release");
    }
}
