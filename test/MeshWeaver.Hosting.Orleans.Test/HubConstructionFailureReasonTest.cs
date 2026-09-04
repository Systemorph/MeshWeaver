using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 THE #1693 DIAGNOSIS, pinned as a contract.
///
/// <para><b>What production actually said.</b> <c>"Hub activation failed for
/// AdvancedBusinessRules: Object reference not set to an instance of an object."</c> — filed as an
/// incident, and unactionable: no line, no node type, no hint that the real exception had already
/// been written with its stack milliseconds earlier.</para>
///
/// <para><b>Where it came from.</b> <c>MessageHubGrain.CompleteActivation</c> called
/// <c>meshHub.GetHostedHub(address, config)</c> and suppressed the nullability with <c>!</c>. That
/// call returns NULL on three real paths — the configuration threw (and
/// <c>HostedHubsCollection.CreateHub</c> caught it, logged <i>"Failed to create hosted hub for
/// address {Address}"</i> WITH the stack, and returned null), the collection is disposing, or an
/// ancestor froze creation. Dereferencing that null threw a <see cref="NullReferenceException"/>
/// which the grain's own catch then reported as the CAUSE of the activation — a secondary failure
/// masking the primary one. The Monolith twin has always been null-safe here
/// (<c>MonolithRoutingService</c>: <c>createdHub?.RegisterForDisposal(...)</c>); the asymmetry is
/// what made this Orleans-only.</para>
///
/// <para>Deleting the <c>!</c> is what makes the null check compiler-enforced under this repo's
/// nullable + warnings-as-errors settings; this test pins the other half — that the message a
/// caller receives is a diagnosis rather than a null dereference.</para>
///
/// <para>🚨 <b>#3243 sharpened it.</b> The reason no longer LISTS the reachable causes, it NAMES
/// the one that happened — <see cref="HostedHubOutcome"/> carries the condition out of
/// <c>HostedHubsCollection</c>, where it is known. Which one it was also decides the log LEVEL;
/// that half is pinned by <see cref="HubConstructionOutcomeReportingTest"/>.</para>
/// </summary>
public class HubConstructionFailureReasonTest
{
    /// <summary>
    /// It names the node and its type. That is the minimum needed to know WHICH package broke,
    /// which is precisely what the bare NRE withheld.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheReason_NamesTheNodeAndItsType()
    {
        var reason = MessageHubGrain.HubConstructionFailureReason(
            new MeshNode("AdvancedBusinessRules") { NodeType = "Store/Plugin" },
            HostedHubOutcome.ConstructionFaulted);

        reason.Should().Contain("AdvancedBusinessRules").And.Contain("Store/Plugin");
        reason.Should().NotContain("Object reference not set",
            "a null dereference inside the grain is a SECOND failure that masks the first — the "
            + "message must describe hub construction, not the crash that reporting it caused");
    }

    /// <summary>
    /// 🚨 It points at where the REAL exception is. The cause is caught, logged with its stack and
    /// swallowed one layer down, so a reader who does not know that goes looking for a stack that
    /// is not attached to anything — which is exactly how #1693 was triaged as a route defect.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheReason_PointsAtTheLogEntryCarryingTheRealException()
    {
        var reason = MessageHubGrain.HubConstructionFailureReason(
            new MeshNode("AdvancedBusinessRules") { NodeType = "Store/Plugin" },
            HostedHubOutcome.ConstructionFaulted);

        reason.Should().Contain("Failed to create hosted hub",
            "that is the verbatim HostedHubsCollection log entry that carries the actual stack");

        // 🚨 #3243: the reader no longer has to tell the two apart from one sentence — the two
        // conditions produce two DIFFERENT sentences, and only one of them mentions a shutdown.
        var shuttingDown = MessageHubGrain.HubConstructionFailureReason(
            new MeshNode("AdvancedBusinessRules") { NodeType = "Store/Plugin" },
            HostedHubOutcome.HostShuttingDown);

        shuttingDown.Should().Contain("shutting down",
            "a reader must be able to tell a broken configuration from a pod that is going away");
        shuttingDown.Should().NotBe(reason,
            "reporting both conditions with one sentence is exactly what #3243 removed");
    }

    /// <summary>
    /// A node with no NodeType still produces a readable sentence rather than a hole — the untyped
    /// node is one of the shapes that reaches hub construction in the first place.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void AnUntypedNode_StillReadsAsASentence()
    {
        var reason = MessageHubGrain.HubConstructionFailureReason(
            new MeshNode("Orphan"), HostedHubOutcome.ConstructionFaulted);

        reason.Should().Contain("Orphan").And.Contain("(null)");
    }
}
