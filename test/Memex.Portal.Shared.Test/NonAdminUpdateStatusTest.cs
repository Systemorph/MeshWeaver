using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The access question behind the About tab's "is this build current?" line, answered against a REAL
/// mesh rather than assumed.
///
/// <para><b>The finding:</b> an ordinary user CANNOT read <c>Admin/UpdatePolicy</c> — the per-user read
/// gate in <c>MeshNodeStreamCache</c> evaluates the caller's effective permissions on the path and
/// throws <see cref="UnauthorizedAccessException"/> when <c>Read</c> is absent. So the running-vs-latest
/// comparison could not simply be moved onto the ungated page: it had to become a PROJECTION
/// (<see cref="PlatformUpdateStatus.Observe"/>) that reads the node as System and lets only the verdict
/// out — never the policy, the CI-green flag or the poller's bookkeeping.</para>
///
/// <para>🚨 This class deliberately builds on <c>ConfigureMeshBase</c>, NOT <c>base.ConfigureMesh</c>.
/// The latter chains <c>TestUsers.PublicAdminAccess()</c>, which grants Public the Admin role in the
/// <c>Admin</c> namespace — under it every identity can read <c>Admin/*</c> and every assertion here
/// would pass vacuously.</para>
/// </summary>
public class NonAdminUpdateStatusTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A tag far above any real platform version, so "newer" is unambiguous.</summary>
    private const string NewerTag = "9999.0.0-ci.1";

    /// <summary>The build the comparison runs against — explicit, because under a test host the entry
    /// assembly's informational version is the runner's, not a platform version.</summary>
    private const string RunningVersion = "3.0.0-ci.2340";

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // No PublicAdminAccess — see the class remarks.
        => ConfigureMeshBase(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// Seeds <c>Admin/UpdatePolicy</c> with a newer tag recorded, as System — the same identity the
    /// poller writes it under (<c>SelfUpdateHostedService.RecordAvailable</c>).
    ///
    /// <para>🚨 The System scope is opened and closed SYNCHRONOUSLY around the subscribe, the same
    /// shape <see cref="PlatformUpdateStatus.Observe"/> uses and for the same reason: impersonation is
    /// an <c>AsyncLocal</c>, and <c>Observable.Using</c> would dispose it on whichever thread the
    /// create terminates on — never returning the calling thread's <c>Context</c> to what it was.
    /// A seed that leaks <c>system-security</c> onto the test thread makes every later assertion run
    /// as System and pass while proving nothing, which is exactly what the first draft of this class
    /// did. (<c>MonolithMeshTestBase.SeedTopLevel</c> still has the leaky shape — worth a look.)</para>
    /// </summary>
    private Task<MeshNode> SeedPolicy(UpdatePolicyKind policy = UpdatePolicyKind.Continuous,
        string? latestAvailableTag = NewerTag)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = policy, LatestAvailableTag = latestAvailableTag },
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(ReadBudget)
            .ToTask(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Switches to a user with NO grants anywhere — the ordinary-viewer shape.
    ///
    /// <para>🚨 BOTH contexts, deliberately. Readers resolve identity as
    /// <c>Context ?? CircuitContext</c>, so the request-scoped <c>Context</c> outranks the circuit —
    /// a circuit-only switch can be shadowed by whatever last wrote <c>Context</c> and leave the test
    /// asserting against the wrong user. Setting both makes the identity unambiguous no matter what
    /// ran before; the <see cref="EffectivePermissions"/> guard in each test proves it took.</para>
    /// </summary>
    private void BecomeOrdinaryUser() => Become(new AccessContext
    {
        ObjectId = "ordinary-viewer",
        Name = "Ordinary Viewer",
    });

    /// <summary>Back to the harness admin (root grant) — same both-contexts reasoning as above.</summary>
    private void BecomeAdmin() => Become(TestUsers.Admin);

    private void Become(AccessContext user)
    {
        Access.SetContext(user);
        Access.SetCircuitContext(user);
    }

    /// <summary>The identity the mesh actually sees, so no test here can assert against the wrong user.</summary>
    private Task<Permission> EffectivePermissions(string userId) =>
        Mesh.GetEffectivePermissions(UpdatePolicyNodeType.NodePath, userId)
            .FirstAsync()
            .Timeout(ReadBudget)
            .ToTask(TestContext.Current.CancellationToken);

    /// <summary>Reads the policy node the way any consumer would, capturing whichever way it fails.</summary>
    private async Task<(MeshNode? Node, Exception? Error)> TryReadPolicyNode()
    {
        try
        {
            var node = await Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Where(n => n is not null)
                .FirstAsync()
                .Timeout(ReadBudget)
                .ToTask(TestContext.Current.CancellationToken);
            return (node, null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// Positive control: the node really is there and really does carry the tag — so the denial in the
    /// next test is a denial, not an empty mesh.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task An_admin_can_read_the_policy_node()
    {
        await SeedPolicy();
        BecomeAdmin();

        (await EffectivePermissions(TestUsers.Admin.ObjectId!)).Should().HaveFlag(Permission.Read);

        var (node, error) = await TryReadPolicyNode();

        error.Should().BeNull();
        UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions)
            .LatestAvailableTag.Should().Be(NewerTag);
    }

    /// <summary>
    /// The finding this feature had to be designed around: the Admin partition is platform-scoped, so
    /// an ordinary user's read of the policy node is REFUSED. Two assertions — the security property
    /// (the tag never reaches them) and the shape of the refusal (it errors rather than hanging, which
    /// is what lets <see cref="PlatformUpdateStatus.Observe"/> degrade instead of stalling the page).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task An_ordinary_user_cannot_read_the_policy_node()
    {
        await SeedPolicy();
        BecomeOrdinaryUser();

        // Non-vacuity guard: prove the identity in force really holds nothing on this path. Without
        // it, a leaked System scope would make every assertion below pass while testing nobody.
        (await EffectivePermissions("ordinary-viewer")).Should().Be(Permission.None);

        var (node, error) = await TryReadPolicyNode();

        UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions)
            .LatestAvailableTag.Should().NotBe(NewerTag,
                "the Admin partition holds platform data an ordinary user has no grant on");
        error.Should().BeOfType<UnauthorizedAccessException>(
            "the read gate refuses rather than emitting an empty node — a hang would stall the About page");
    }

    /// <summary>
    /// …and yet that same ordinary user gets the answer, because the About tab consumes a PROJECTION
    /// read as System. The tag is what they see; nothing else on the node is.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task An_ordinary_user_sees_the_derived_update_status()
    {
        await SeedPolicy();
        BecomeOrdinaryUser();

        (await EffectivePermissions("ordinary-viewer")).Should().Be(Permission.None);

        var status = await PlatformUpdateStatus.Observe(Mesh, RunningVersion)
            .FirstAsync()
            .Timeout(ReadBudget)
            .ToTask(TestContext.Current.CancellationToken);

        status.Availability.Should().Be(PlatformUpdateAvailability.UpdateAvailable);
        status.LatestVersion.Should().Be(NewerTag);
    }

    /// <summary>
    /// An install that never wired self-update has no policy node at all. That must resolve to
    /// <see cref="PlatformUpdateAvailability.Unknown"/> — promptly, via the empty-on-absent existence
    /// query, never a point-read of the missing path (which NotFound-resubscribe-storms) and never a
    /// claim of currency nobody checked.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task With_no_policy_node_the_status_is_unknown()
    {
        BecomeOrdinaryUser();

        var status = await PlatformUpdateStatus.Observe(Mesh, RunningVersion)
            .FirstAsync()
            .Timeout(ReadBudget)
            .ToTask(TestContext.Current.CancellationToken);

        status.Should().Be(PlatformUpdateStatus.Unknown);
    }
}
