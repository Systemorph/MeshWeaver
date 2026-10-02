using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Drives the broad-grant guard THROUGH the create write boundary (<c>BroadGrantRejection</c> in
/// <c>MeshExtensions</c>), not only through its pure predicates (<see cref="BroadGrantGuardTest"/>).
/// The enforcement is the security-relevant half of the guard, and its setting flips from
/// <see cref="BroadGrantMode.LogOnly"/> to <see cref="BroadGrantMode.Enforce"/>. These tests pin
/// what each mode does to the SAME write, so the flip changes exactly one thing.
///
/// <para>Every write here runs as System, so the RLS validators after the guard are bypassed and the
/// guard is the only thing that can refuse. A refusal therefore IS the guard's refusal, and its
/// catalog key is asserted so it cannot be some other validator's.</para>
/// </summary>
public abstract class BroadGrantGuardAtTheWriteBoundaryTestBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The executor's activity, as the Governance package writes it.</summary>
    protected const string ActivityPath = "Governance/Activities/grant-public-guide";

    /// <summary>The platform identity with no statement of why it writes: the sweep's shape.</summary>
    protected static readonly AccessContext System = new()
    {
        ObjectId = WellKnownUsers.System,
        Name = WellKnownUsers.System,
    };

    /// <summary>The executor's identity: System, stamped with the activity it is executing.</summary>
    protected static readonly AccessContext Executor = System with { GovernedBy = ActivityPath };

    /// <summary>A grant of Viewer at <paramref name="scope"/>, with the executor's back-reference when given.</summary>
    protected static MeshNode Grant(string scope, string subject, string? governedBy = null) =>
        new($"{subject}_Access", $"{scope}/_Access")
        {
            Name = $"{subject} access",
            NodeType = AccessAssignmentGuard.AccessAssignmentNodeType,
            MainNode = scope,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = subject,
                Roles = [new RoleAssignment { Role = "Viewer" }],
                GovernedBy = governedBy,
            },
        };

    /// <summary>Writes the activity node the default verifier reads, in the given state.</summary>
    protected Task WriteActivity(string state, string standard = "Governance/Standards/access.grant-broad") =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>().Write(
                new MeshNode("grant-public-guide", "Governance/Activities")
                {
                    Name = "Grant the guide to everyone",
                    NodeType = "Activity",
                    State = MeshNodeState.Active,
                    Content = new JsonObject { ["state"] = state, ["standard"] = standard },
                },
                Mesh.JsonSerializerOptions)
            .Should().Emit();

    /// <summary>
    /// A node a control plane ACTS on because it names an activity — the shape of
    /// <c>Admin/Provision/{package}</c> — with <c>governedBy</c> set to <paramref name="claim"/>.
    /// Not a grant, so the broad-grant shapes never see it: only the governed-claim check can.
    /// </summary>
    protected static MeshNode ClaimingNode(string scope, string id, string? claim) =>
        new(id, scope)
        {
            Name = $"request {id}",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = claim is null
                ? new JsonObject { ["packageId"] = id }
                : new JsonObject { ["packageId"] = id, ["governedBy"] = claim },
        };

    /// <summary>Upserts <paramref name="node"/> as <paramref name="writer"/>; the response either way.</summary>
    protected Task<CreateOrUpdateNodeResponse> Upsert(MeshNode node, AccessContext writer) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(writer, () => ObserveNodeOperation(new CreateOrUpdateNodeRequest(node)))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit($"the upsert of {node.Path} must settle either way");

    /// <summary>
    /// Creates <paramref name="node"/> as <paramref name="writer"/> and returns the failure, or null
    /// when the create succeeded. Both outcomes are an emission, so a hang is a failure, not a pass.
    /// </summary>
    protected Task<Exception?> Create(MeshNode node, AccessContext writer) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(writer, () => NodeFactory.CreateNode(node))
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Should().Within(TestTimeouts.Convergence).Emit($"the create of {node.Path} must settle either way");
}

/// <summary>The guard in <see cref="BroadGrantMode.Enforce"/>: the mode the setting flips to.</summary>
public class BroadGrantGuardEnforcedAtTheWriteBoundaryTest(ITestOutputHelper output)
    : BroadGrantGuardAtTheWriteBoundaryTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            // LAYER the mode onto the host's configuration, never replace it: a bare
            // AddSingleton<IConfiguration> would drop every other key the mesh needs to boot.
            var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
            if (configured is not null)
                services.Remove(configured);
            return services.AddSingleton<IConfiguration>(sp =>
            {
                var builder = new ConfigurationBuilder();
                if (Materialise(configured, sp) is { } host)
                    builder.AddConfiguration(host);
                return builder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [BroadGrantGuard.ModeKey] = nameof(BroadGrantMode.Enforce),
                }).Build();
            });
        });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer the guard's mode onto it.");

    /// <summary>The 2026-09-29 shape: a System grant to everybody, with no governed activity behind it.</summary>
    [Fact]
    public async Task AnUngovernedGrantToEveryone_IsRefused_WithTheGuardsKeyedRefusal()
    {
        var grant = Grant("broadgrantenforced", WellKnownUsers.Public);

        var failure = await Create(grant, System);

        failure.Should().NotBeNull("Enforce refuses a broad grant that no executing governed activity accounts for");
        var refusal = failure?.RefusalText();
        refusal.Should().NotBeNull("the refusal must travel keyed, so a German viewer reads it in German");
        (refusal?.MessageKey).Should().Be(BroadGrantGuard.RefusalKey,
            "every writer here is System, so no validator after the guard can have refused it");
        (refusal?.Message ?? "").Should().Contain(grant.Path);
        (refusal?.Message ?? "").Should().Contain(BroadGrantGuard.StandardsKey,
            "the refusal names the allowlist setting instead of a list that can go stale");
    }

    /// <summary>The one way through: the executor's context and the grant name one EXECUTING activity.</summary>
    [Fact]
    public async Task AGrantByAnExecutingGovernedActivity_Passes()
    {
        await WriteActivity("Executing");

        var failure = await Create(Grant("broadgrantgoverned", WellKnownUsers.Public, ActivityPath), Executor);

        failure.Should().BeNull("an executing allowlisted activity is the one writer a broad grant is for");
    }

    /// <summary>
    /// The negative control for <see cref="AGrantByAnExecutingGovernedActivity_Passes"/>: the same
    /// claim and the same grant, with an activity that is only READY. If the verifier were not
    /// consulted, or read nothing and defaulted to true, this would pass too.
    /// </summary>
    [Fact]
    public async Task AGrantClaimingAnActivityThatIsNotExecuting_IsRefused()
    {
        await WriteActivity("Ready");

        var failure = await Create(Grant("broadgrantready", WellKnownUsers.Public, ActivityPath), Executor);

        failure.Should().NotBeNull("an activity that is not executing authorises nothing");
        (failure?.RefusalText()?.MessageKey).Should().Be(BroadGrantGuard.RefusalKey);
    }

    /// <summary>
    /// The finding this pins (Plugins#2601 review): a writer with standing rights creates a node that
    /// merely CLAIMS a governed activity. The activity really is executing — signed for something
    /// else — so a reader that checks only "is the named activity executing" is fooled. The write
    /// boundary refuses it because the writer is not that activity.
    /// </summary>
    [Fact]
    public async Task ANodeClaimingAnActivityItsWriterIsNotExecuting_IsRefused()
    {
        await WriteActivity("Executing", "Governance/Standards/package.provision");

        var failure = await Create(ClaimingNode("governedclaimspoof", "Parties", ActivityPath), System);

        failure.Should().NotBeNull("a governedBy back-reference is written only by the activity it names");
        (failure?.RefusalText()?.MessageKey).Should().Be(BroadGrantGuard.ClaimRefusalKey,
            "every writer here is System, so no validator after the guard can have refused it");
    }

    /// <summary>The executor's own write: context and node name one executing activity.</summary>
    [Fact]
    public async Task ANodeWrittenByTheActivityItClaims_Passes()
    {
        await WriteActivity("Executing", "Governance/Standards/package.provision");

        var failure = await Create(ClaimingNode("governedclaimexecutor", "Reporting", ActivityPath), Executor);

        failure.Should().BeNull("the governance executor writing its own back-reference is the claim made true");
    }

    /// <summary>Negative control: the writer IS the activity, but the activity is not executing.</summary>
    [Fact]
    public async Task ANodeClaimingAnActivityThatIsNotExecuting_IsRefused()
    {
        await WriteActivity("Ready", "Governance/Standards/package.provision");

        var failure = await Create(ClaimingNode("governedclaimready", "Reporting", ActivityPath), Executor);

        (failure?.RefusalText()?.MessageKey).Should().Be(BroadGrantGuard.ClaimRefusalKey,
            "an activity that is not executing authorises nothing");
    }

    /// <summary>
    /// The owner's own bookkeeping rewrites the node with the claim it already carries (the provision
    /// watcher's state stamps): that introduces nothing and must pass, or the check would refuse
    /// every governed request's progress. Changing the claim on the same node is checked again.
    /// </summary>
    [Fact]
    public async Task AnUpsertThatKeepsTheStoredClaim_Passes_AndOneThatChangesIt_IsRefused()
    {
        await WriteActivity("Executing", "Governance/Standards/package.provision");
        (await Create(ClaimingNode("governedclaimkeep", "Reporting", ActivityPath), Executor))
            .Should().BeNull("the executor creates the governed request");

        var kept = await Upsert(ClaimingNode("governedclaimkeep", "Reporting", ActivityPath) with { Name = "running" }, System);
        kept.Success.Should().BeTrue($"keeping the stored claim introduces nothing — {kept.Error}");

        var changed = await Upsert(ClaimingNode("governedclaimkeep", "Reporting", "Governance/Activities/another"), System);
        changed.Success.Should().BeFalse("a changed claim is a new claim, checked like a create");
    }

    /// <summary>A user's own acquisition stamps OnBehalfOf: Enforce must not refuse every System grant.</summary>
    [Fact]
    public async Task ASystemGrantOnBehalfOfItsOwnSubject_Passes()
    {
        var failure = await Create(Grant("broadgrantownacquisition", "jdoe"), System with { OnBehalfOf = "jdoe" });

        failure.Should().BeNull("a subscription, coupon or purchase grants the one user acquiring access");
    }
}

/// <summary>The guard in its default mode, <see cref="BroadGrantMode.LogOnly"/>: it observes and refuses nothing.</summary>
public class BroadGrantGuardLogOnlyAtTheWriteBoundaryTest(ITestOutputHelper output)
    : BroadGrantGuardAtTheWriteBoundaryTestBase(output)
{
    /// <summary>
    /// The same write <see cref="BroadGrantGuardEnforcedAtTheWriteBoundaryTest.AnUngovernedGrantToEveryone_IsRefused_WithTheGuardsKeyedRefusal"/>
    /// refuses. With no mode configured the host is in LogOnly, so the write lands.
    /// </summary>
    [Fact]
    public async Task AnUngovernedGrantToEveryone_IsWritten()
    {
        BroadGrantGuard.Mode(Mesh.ServiceProvider.GetService<IConfiguration>())
            .Should().Be(BroadGrantMode.LogOnly, "the test host configures no mode, so the default applies");

        var failure = await Create(Grant("broadgrantlogonly", WellKnownUsers.Public), System);

        failure.Should().BeNull("LogOnly logs WOULD REFUSE and refuses nothing");
    }

    /// <summary>The claim check follows the same mode: in LogOnly a spoofed claim is logged, not refused.</summary>
    [Fact]
    public async Task ANodeClaimingAnActivityItsWriterIsNotExecuting_IsWritten()
    {
        await WriteActivity("Executing", "Governance/Standards/package.provision");

        var failure = await Create(ClaimingNode("governedclaimlogonly", "Parties", ActivityPath), System);

        failure.Should().BeNull("LogOnly logs WOULD REFUSE GovernedClaim and refuses nothing");
    }
}
