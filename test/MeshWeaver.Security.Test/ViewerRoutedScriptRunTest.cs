#pragma warning disable CS1591

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the escape hatch that lets a NON-ADMIN run a script living in a partition they may read
/// but never write: <c>CodeConfiguration.ActivityParentPath = "{viewer}"</c>, which
/// <see cref="CodeNodeType.ResolveActivityParent"/> expands to the caller's own home.
///
/// <para><b>Why this contract has a test.</b> Issue #1295 was filed automatically from a single
/// production log line on 2026-08-12 12:32:29Z:</para>
///
/// <code>
/// ExecuteScript faulted on Hosting/Script/refresh-status: Could not start the script run.
/// System.UnauthorizedAccessException: Access denied: Create permission required for node
/// 'Hosting/_Activity/3c969a246ba544eeb4590e08680ba622'
/// </code>
///
/// <para>The issue concluded the caller was missing a <c>Create</c> grant on
/// <c>Hosting/_Activity</c> and asked for one. That reading is wrong, and acting on it would have
/// been a security regression: <c>Hosting</c> is a gated PLUGIN partition where only the system
/// identity may write, so granting an ordinary user <c>Create</c> there is precisely what must
/// not happen. Nothing in the framework misbehaved — RLS refused a user write to a partition that
/// forbids user writes, which is the fail-closed behaviour working as designed.</para>
///
/// <para><b>The actual defect was in the plugin's own content</b>, and it was already fixed
/// sixty seconds after the log line: at the time of the run
/// <c>Hosting/Script/refresh-status</c> declared no <c>ActivityParentPath</c> at all, so
/// <see cref="CodeNodeType.ResolveActivityParent"/> fell through to its last layer — the
/// partition root — and aimed the run's Activity at <c>Hosting/_Activity/…</c>, inside the very
/// partition the caller may not write. MeshWeaver.Plugins commit <c>1f81035</c> ("Hosting scripts
/// route their activity to the caller's home", 2026-08-12T14:33:29+02:00 — the incident was
/// 14:32:29+02:00 local) sets <c>"{viewer}"</c> on that node and on <c>ingest-logs</c>, which
/// moves the Activity into the home partition of whoever clicks Run.</para>
///
/// <para>So the cure lives in plugin content, but it only works because of a framework contract
/// that nothing in this repository asserted: that <c>"{viewer}"</c> resolves to the CALLER for a
/// caller holding nothing but <see cref="Role.Viewer"/> on the script's partition. These tests
/// pin that contract, so a future change to identity propagation or to
/// <c>ResolveActivityParent</c>'s layering cannot silently strand every gated-partition script
/// again.</para>
/// </summary>
public class ViewerRoutedScriptRunTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A plain signed-in user — no admin anywhere, and no write grant on the plugin partition.</summary>
    private const string OrdinaryUser = "sglauser";

    /// <summary>Stands in for the gated <c>Hosting</c> plugin partition: readable, never user-writable.</summary>
    private const string PluginNs = "Hosting";

    private const string ScriptPath = $"{PluginNs}/Script/refresh-status";

    // 🚨 ConfigureMeshBase, NOT base.ConfigureMesh — the default adds TestUsers.PublicAdminAccess()
    // (Public → Admin at ROOT), under which the create can never be refused and these tests can
    // never fail.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(PluginNs) { Name = "Hosting", NodeType = "Markdown" },
                new MeshNode("Script", PluginNs) { Name = "Script", NodeType = "Markdown" },
                // The Code node as the Hosting plugin ships it AFTER 1f81035: executable, and
                // routing its run into the VIEWER's home rather than the plugin partition.
                new MeshNode("refresh-status", $"{PluginNs}/Script")
                {
                    Name = "Refresh deployment status",
                    NodeType = CodeNodeType.NodeType,
                    Content = new CodeConfiguration
                    {
                        Code = "System.Console.WriteLine(\"sampled\"); 42",
                        Language = "csharp",
                        IsExecutable = true,
                        ActivityParentPath = "{viewer}"
                    }
                },
                // The user's own home — where a "{viewer}"-routed run must file its activity.
                new MeshNode(OrdinaryUser) { Name = "S Glauser", NodeType = "User" },
                // The ONLY grant: Viewer on the plugin partition. Viewer = Read | Execute | Api,
                // so the user may RUN the script and may NOT create anything under Hosting —
                // exactly the production shape.
                AssignmentNodeFactory.UserRole(OrdinaryUser, Role.Viewer.Id, PluginNs));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private void SignInAsOrdinaryUser()
        => Access.SetContext(new AccessContext { ObjectId = OrdinaryUser, Name = "S Glauser" });

    private void SignOut() => Access.SetContext(null);

    /// <summary>
    /// The regression pin for #1295: a non-admin runs a gated-partition script declared
    /// <c>ActivityParentPath = "{viewer}"</c>; the run is accepted and its Activity lands in the
    /// CALLER's home, never in the plugin partition.
    ///
    /// <para>Landing under <c>Hosting/_Activity/</c> instead is the production failure exactly —
    /// it means the <c>"{viewer}"</c> layer did not resolve to the caller, and the create is then
    /// refused by a partition that (correctly) forbids user writes.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ViewerRoutedScript_RunByNonAdmin_FilesItsActivityInTheCallersHome()
    {
        SignInAsOrdinaryUser();
        try
        {
            var dispatch = (await Mesh
                .Observe<ExecuteScriptResponse>(
                    new ExecuteScriptRequest(), o => o.WithTarget(new Address(ScriptPath)))
                .Should().Within(60.Seconds()).Emit()).Message;

            dispatch.Success.Should().BeTrue(
                "a caller holding only Viewer may still RUN the script — its Activity is routed to "
                + "their own home by ActivityParentPath = \"{viewer}\", so no write ever lands in "
                + $"the gated partition. Error: {dispatch.Error}");

            // ActivityLog is nullable in the contract; assert it is populated FIRST so a
            // regression reads as "the dispatch reported no activity path" rather than as a
            // baffling string comparison against null.
            dispatch.ActivityLog.Should().NotBeNullOrEmpty(
                "a successful dispatch must report the path its Activity actually landed at");

            dispatch.ActivityLog.Should().StartWith($"{OrdinaryUser}/_Activity/",
                "\"{viewer}\" must expand to the CALLER's home. Landing under "
                + $"'{PluginNs}/_Activity/' is the #1295 production failure: the run's Activity is "
                + "aimed at a partition only the system identity may write, and the create is "
                + "refused with \"Access denied: Create permission required\"");
        }
        finally
        {
            SignOut();
        }
    }

    /// <summary>
    /// The Activity is attributed to the caller, not to an infrastructure principal. A hub- or
    /// system-stamped <c>CreatedBy</c> here would mean the run authorised itself by picking up an
    /// ambient identity rather than the user's — an escalation dressed up as a passing test.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ViewerRoutedScript_StampsItsActivityWithTheCallingUser()
    {
        SignInAsOrdinaryUser();
        try
        {
            var dispatch = (await Mesh
                .Observe<ExecuteScriptResponse>(
                    new ExecuteScriptRequest(), o => o.WithTarget(new Address(ScriptPath)))
                .Should().Within(60.Seconds()).Emit()).Message;

            dispatch.Success.Should().BeTrue(dispatch.Error ?? "dispatch was refused");
            dispatch.ActivityLog.Should().NotBeNullOrEmpty(
                "the Activity path is what this test reads the stamp from");

            var activity = await Mesh.GetMeshNode(dispatch.ActivityLog!)
                .Where(node => node is not null)
                .FirstAsync()
                .Timeout(30.Seconds())
                .ToTask();

            activity!.CreatedBy.Should().Be(OrdinaryUser,
                "the run's Activity belongs to the user who started it");
        }
        finally
        {
            SignOut();
        }
    }
}
