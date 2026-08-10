using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 #1127: pins <see cref="AiSourcesInstallHook"/>'s user enumeration against the SELF-TYPED
/// <c>User</c> NodeType declaration node.
///
/// <para><c>User</c> is deliberately self-typed (<see cref="UserNodeType.CreateMeshNode"/> sets
/// <c>NodeType = "User"</c> on the declaration at path <c>User</c>), so the hook's pathless
/// <c>nodeType:User</c> enumeration returns the DECLARATION alongside the real accounts. Pre-fix,
/// the declaration's Id (<c>"User"</c>) was treated as an account id, and every install / boot
/// repair pass tried to write <c>User/_Memex/AiSettings</c> — a partition literally named
/// <c>User</c> that no instance provisions. On PostgreSQL that failed each time with
/// <c>42P01: relation "user.mesh_nodes" does not exist</c> (27× on memex-cloud: 3 pod boots ×
/// ~9 installed packages, the per-user Catch reducing it to log noise); on stores without
/// partition schemas it silently created a phantom node. The failing writes carried nothing a
/// real user ever entered — no user data was lost — but the noise re-fired on every boot.</para>
///
/// <para>The fix is in the enumeration (<c>AiSourcesInstallHook.Users()</c>): the declaration id
/// is dropped before reconciliation. This test seeds real accounts, runs the hook end-to-end on
/// the monolith mesh (where the phantom write would SUCCEED and leave evidence), and asserts the
/// package sources land on real users while <c>User/_Memex/AiSettings</c> is never created.</para>
/// </summary>
public class AiSourcesInstallHookTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "HookPkg";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .AddSampleUsers();

    [Fact(Timeout = 60_000)]
    public async Task InstallHook_RegistersSourcesForRealUsers_NeverForTheUserTypeDeclaration()
    {
        var hook = Mesh.ServiceProvider.GetServices<IPartitionInstallHook>()
            .OfType<AiSourcesInstallHook>()
            .Single();

        await hook.OnPartitionInstalled(Package)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask();

        // A real account got the package's agent + skill sources — proves the hook enumerated
        // users and wrote (the filter must not over-filter real accounts).
        var realUserPath = AiSettingsNodeType.PathFor("TestUser");
        var settingsNodes = await Mesh.GetWorkspace()
            .GetQuery($"{AiSettingsNodeType.NodeType}|TestUser",
                $"path:{realUserPath} nodeType:{AiSettingsNodeType.NodeType} select:path,id,name,nodeType,content")
            .Where(nodes => nodes.Any(n =>
                string.Equals(n.NodeType, AiSettingsNodeType.NodeType, StringComparison.OrdinalIgnoreCase)))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask();

        var settings = AiSettingsNodeType.Effective(
            settingsNodes.First(n =>
                string.Equals(n.NodeType, AiSettingsNodeType.NodeType, StringComparison.OrdinalIgnoreCase)),
            new AiSettings(),
            Mesh.JsonSerializerOptions);
        settings.AgentQueries.Should().Contain(
            q => q.Contains($"{Package}/", StringComparison.OrdinalIgnoreCase),
            "the install hook must register the package's agent source on every real account");
        settings.SkillQueries.Should().Contain(
            q => q.Contains($"{Package}/", StringComparison.OrdinalIgnoreCase),
            "the install hook must register the package's skill source on every real account");

        // The self-typed `User` NodeType DECLARATION must never be reconciled as an account:
        // no node at User/_Memex/AiSettings. Pre-fix this write happened once per package per
        // boot — 42P01 noise on PostgreSQL, a phantom node here (#1127).
        var phantomPath = AiSettingsNodeType.PathFor(UserNodeType.NodeType);

        // 🚨 Read the NODE, not the index. This assertion pins an ABSENCE immediately after a write
        // pass, which is the one case a query cannot settle: GetQuery is eventually consistent, so
        // an empty first emission is equally consistent with "the phantom was never written" and
        // "the phantom was written and the index has not caught up yet". The second reading is the
        // regression this test exists to catch, so a query here can only ever pass — including on
        // the pre-fix code. GetMeshNodeStream is authoritative, and an absent node surfaces as a
        // routing NotFound (OnError/DeliveryFailureException), which is a DIFFERENT signal from a
        // present one (OnNext) rather than the same empty result. Same shape as
        // CompileActivityNoPhantomPathTest, which pins the identical "leaf never created under a
        // real ancestor" phantom.
        var probe = await Mesh.GetMeshNodeStream(phantomPath)
            .Where(n => n?.Content is not null)
            .Materialize()
            .Should().Within(TimeSpan.FromSeconds(15)).Match(
                n => n.Kind == NotificationKind.OnError,
                $"the `User` NodeType declaration node is not an account — anything served at "
                + $"{phantomPath} means the enumeration treated the self-typed declaration as a "
                + "user again (#1127)");

        probe.Exception.Should().BeOfType<DeliveryFailureException>(
            "the absence must be the routing NotFound for a node that was never created — any other "
            + "fault would mean this assertion passed for a reason unrelated to #1127");
    }
}
