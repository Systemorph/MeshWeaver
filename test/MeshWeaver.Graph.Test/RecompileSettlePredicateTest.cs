using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for <see cref="NodeTypeEnrichmentHelpers.IsRecompileSettled"/> — the predicate
/// that decides when the self-heal recompile started by <c>TriggerRecompileAndRetry</c> has
/// actually ANSWERED, as opposed to the stream merely replaying the state we already rejected.
///
/// <para><b>The bug this pins.</b> The Ok→Pending flip that starts the recompile is a cross-hub
/// JSON-merge patch and does not round-trip synchronously, so for a few ms the NodeType stream
/// still replays the PRE-FLIP node. The bytes-missing path had no version gate, so its "terminal
/// Ok with assembly coordinates" clause matched that stale replay 0 ms after the flip. The retry
/// therefore burned its single attempt before the recompile it had just kicked off even started,
/// concluded "still missing", and bound the silent default-config fallback wrapped in a
/// <c>WithOverlaySelfHeal</c> watcher gated at that same version. The recompile then landed a
/// NEWER usable build — exactly the watcher's trigger — so the watcher recycled the instance hub
/// milliseconds after it was created, taking the request that triggered the activation with it.
/// Observed end-to-end as <c>ThreadAgentIntegrationTest</c>'s 60 s
/// <c>GetMeshNode('ACME/ProductLaunch')</c> stall (create at T+0, self-dispose at T+13 ms, reader
/// idle until its budget expired).</para>
/// </summary>
public class RecompileSettlePredicateTest
{
    private const string NodeTypePath = "TestData/RecompileType";

    private static MeshNode TypeNode(
        CompilationStatus? status,
        long version,
        string? collection = "local",
        string? assemblyPath = "TestData_RecompileType/v1-abc.dll")
        => new(NodeTypePath)
        {
            Version = version,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = status,
                LatestAssemblyCollection = collection,
                LatestAssemblyPath = assemblyPath,
                CompiledFrameworkVersion = "some-old-framework",
            }
        };

    [Fact]
    public void StaleReSnapOfTheVersionWeAlreadyRejected_IsNotSettled()
    {
        // The bytes-missing heal failed on version 20 and flipped Ok→Pending. Before that flip
        // round-trips, the stream replays version 20 unchanged. Accepting it here is the defect.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Ok, version: 20), staleVersion: 20, requireUsableBuild: false)
            .Should().BeFalse(
                "re-snapping the exact state whose assembly we already failed to resolve is not "
                + "news — it burns the single retry attempt before the recompile even starts");

        // A version BEHIND the one we rejected is an even older replay; same verdict.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Ok, version: 19), staleVersion: 20, requireUsableBuild: false)
            .Should().BeFalse();
    }

    [Fact]
    public void VersionAdvancePastTheRejectedState_IsSettled()
    {
        // The recompile landed: a NEW version carrying assembly coordinates. This is the genuine
        // answer the wait exists for, and it must be accepted or activation would hang.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Ok, version: 21), staleVersion: 20, requireUsableBuild: false)
            .Should().BeTrue();

        // A real compile failure at an advanced version is also terminal — the caller overlays it.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Error, version: 21), staleVersion: 20, requireUsableBuild: false)
            .Should().BeTrue();
    }

    [Fact]
    public void InFlightCompile_IsNeverSettled_EvenPastTheVersionGate()
    {
        // Pending/Compiling are transitional. Settling on them would freeze the instance's
        // HubConfiguration onto a mid-recompile state for the hub's whole lifetime.
        foreach (var status in new[] { CompilationStatus.Pending, CompilationStatus.Compiling })
            NodeTypeEnrichmentHelpers
                .IsRecompileSettled(TypeNode(status, version: 99), staleVersion: 20, requireUsableBuild: false)
                .Should().BeFalse($"{status} is an in-flight compile, not a terminal state");
    }

    [Fact]
    public void WithoutAVersionGate_ATerminalOkIsStillSettled()
    {
        // The framework-stale path and any caller with no rejected version in hand pass null;
        // the gate must then be inert, preserving the pre-existing behaviour.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Ok, version: 1), staleVersion: null, requireUsableBuild: false)
            .Should().BeTrue();
    }

    [Fact]
    public void RequireUsableBuild_IgnoresTheVersionGate_AndRidesThroughTransientErrors()
    {
        // The framework-stale heal keeps its own, stricter rule: only a genuinely usable build
        // counts. A stale Ok can never satisfy it (its CompiledFrameworkVersion does not match),
        // and a transient collision Error must be ridden through rather than frozen onto the
        // instance — so neither is settled, regardless of version.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Ok, version: 99), staleVersion: null, requireUsableBuild: true)
            .Should().BeFalse("the assembly is still compiled against a different framework");

        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Error, version: 99), staleVersion: null, requireUsableBuild: true)
            .Should().BeFalse(
                "an Error during the framework-stale rebuild is transient (a concurrent self-heal "
                + "collision); settling on it froze instances on the overlay for their whole lifetime");
    }

    [Fact]
    public void Unavailable_IsTerminal_SoTheWaitCannotBurnItsWholeBudget()
    {
        // CompilationStatus.Unavailable records that a driver already GAVE UP determining
        // the state (a settle or lookup timeout). No further write is coming, so treating
        // it as unsettled would hold the activation for the whole no-progress budget and
        // then surface the same overlay anyway.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Unavailable, version: 21), staleVersion: 20, requireUsableBuild: false)
            .Should().BeTrue("nothing further will be written for an undetermined state");

        // …but it is still not a USABLE build, so the framework-stale heal keeps waiting.
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(TypeNode(CompilationStatus.Unavailable, version: 99), staleVersion: null, requireUsableBuild: true)
            .Should().BeFalse("requireUsableBuild accepts only a genuinely loadable build");
    }

    [Fact]
    public void NonNodeTypeDefinitionContent_IsSettled_SoTheWaitCannotHang()
    {
        // Nothing left to wait on: the path is not a NodeTypeDefinition at all.
        var plain = new MeshNode(NodeTypePath) { Version = 5, Content = "not a node type definition" };
        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(plain, staleVersion: 20, requireUsableBuild: false)
            .Should().BeTrue();
    }
}
