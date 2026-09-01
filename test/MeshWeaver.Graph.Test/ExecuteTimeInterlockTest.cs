using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The execute-time interlock at the ARMING site (Systemorph/MeshWeaver#2820), driven through the
/// real <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> on a real Monolith mesh — never
/// a mocked hub.
///
/// <para><b>Why this shape.</b> Binding a NodeType's <c>HubConfiguration</c> onto an instance node
/// is the single act that ARMS a type: everything its compiled code can do — handlers, layout
/// areas, <c>WithInitialization</c> watchers, writes — reaches the mesh through the per-instance
/// hub that <c>MonolithRoutingService</c> / <c>MessageHubGrain</c> build from that delegate. So the
/// property under test is not "a log line was written" but "the delegate the instance hub would be
/// built from is the REFUSAL, and it says why".</para>
///
/// <para>🚨 <b>It is a controlled experiment, not a single observation.</b> Both cases persist the
/// SAME NodeType node — same status, same assembly coordinates, same fingerprints — and differ in
/// exactly one field, <see cref="NodeTypeDefinition.BuildProvenance"/>. A test that only asserted
/// the refusal would pass just as well against a gate that refused everything, which is the failure
/// mode that actually takes a portal down; the
/// <see cref="AnUnverifiedAdoption_StillArms_TheAntiOutageProperty"/> half is what discriminates.
/// The observable is the <see cref="UnhandledMessageNack"/> the overlay installs, read back by
/// applying the produced configuration to a bare <see cref="MessageHubConfiguration"/> — the same
/// value a caller's <c>DeliveryFailure</c> would carry.</para>
/// </summary>
public class ExecuteTimeInterlockTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan EnrichBudget = TimeSpan.FromSeconds(20);

    private static MeshConfiguration EmptyMeshConfiguration() => new(Array.Empty<MeshNode>());

    /// <summary>
    /// A NodeType whose compile state is SETTLED and usable-looking — <c>Ok</c> with assembly
    /// coordinates, which is exactly the shape <c>PrebuiltAssemblySeeder.Seed</c> stamps when it
    /// adopts a bundle. The fingerprints disagree, which is what a refusal records.
    /// </summary>
    private static NodeTypeDefinition Adopted(BuildProvenance provenance) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "prebuilt/v1.dll",
            LastCompiledVersion = 1,
            AdoptedSourceFingerprint = "bundlefingerprint01",
            CurrentSourceFingerprint = "livefingerprint02",
            BuildProvenance = provenance,
        };

    /// <summary>
    /// 🚨 THE REFUSAL. The bundle states which sources produced these bytes and they are not the
    /// ones this mesh holds, so the instance is NOT armed with them: the configuration it would
    /// have activated with is the refusal overlay, and every typed request to it fails with
    /// <see cref="ErrorType.ExecutionRefused"/> naming the type.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AProvenStaleAdoption_IsNotArmed_AndSaysSo()
    {
        var nack = await EnrichAgainst(nameof(AProvenStaleAdoption_IsNotArmed_AndSaysSo),
            BuildProvenance.AdoptionRefused);

        nack.Should().NotBeNull(
            "a refused type must still ACTIVATE — with a configuration that refuses — so callers "
            + "get a terminal verdict instead of parking on a hub that never comes up");
        nack!.ErrorType.Should().Be(ErrorType.ExecutionRefused,
            "the caller must be told the bytes were REFUSED. CompilationFailed would send them to "
            + "edit source Roslyn never rejected (#641) and Unavailable would read as 'retry' when "
            + "a verdict was in fact reached (#2818)");
        nack.NodeTypePath.Should().Contain(nameof(AProvenStaleAdoption_IsNotArmed_AndSaysSo),
            "the failure names WHICH type was refused, as CompilationInProgress already does");
        // The summary shortens each fingerprint to its first 12 characters — enough to identify a
        // build, short enough to read in a log line and a page title.
        nack.Reason.Should().Contain("bundlefinger",
            "the bundle's recorded fingerprint travels with the refusal");
        nack.Reason.Should().Contain("livefingerpr",
            "and so does the live one it disagreed with, so an operator can check it by hand");
        nack.Reason.Should().Contain("compile",
            "a refusal with no recovery verb becomes 'the portal is broken'");
    }

    /// <summary>
    /// 🚨 THE ANTI-OUTAGE PROPERTY, and the half that makes the test above mean something. Same
    /// node, one field different: a bundle published before producers recorded a fingerprint adopts
    /// as <see cref="BuildProvenance.AdoptedUnverified"/>, and it must ARM. Refusing here would
    /// park every legacy type on every mesh — and on <c>Modules:RequirePrebuilt</c> there is no
    /// local compile to recover with.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(BuildProvenance.AdoptedUnverified)]
    [InlineData(BuildProvenance.AdoptedVerified)]
    [InlineData(BuildProvenance.Compiled)]
    public async Task AnUnverifiedAdoption_StillArms_TheAntiOutageProperty(BuildProvenance provenance)
    {
        var nack = await EnrichAgainst(
            $"{nameof(AnUnverifiedAdoption_StillArms_TheAntiOutageProperty)}{provenance}",
            provenance);

        // This mesh has no compilation service and no bytes behind those coordinates, so the
        // enrichment lands on one of the ORDINARY availability outcomes. Which one does not
        // matter; what matters is that it is never the execute-time refusal.
        (nack?.ErrorType).Should().NotBe(ErrorType.ExecutionRefused,
            $"{provenance} is not PROVEN stale, so the execute-time interlock must not fire — "
            + "this is the assertion that stops the gate being tightened into an outage");
    }

    /// <summary>
    /// Persists a NodeType node carrying <paramref name="provenance"/>, enriches a fresh instance
    /// of it through the real enrichment path, and returns the <see cref="UnhandledMessageNack"/>
    /// the resulting configuration installs (null when it installs none).
    /// </summary>
    private async Task<UnhandledMessageNack?> EnrichAgainst(string typeName, BuildProvenance provenance)
    {
        var typePath = $"{TestPartition}/{typeName}";
        await CreateAsSystem(new MeshNode(typeName, TestPartition)
        {
            NodeType = MeshNode.NodeTypePath,
            Content = Adopted(provenance),
        });

        var enriched = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(
                Mesh,
                EmptyMeshConfiguration(),
                compilationService: null,
                new MeshNode($"instance-{provenance}", TestPartition) { NodeType = typePath })
            .Take(1)
            .Should().Within(EnrichBudget).Emit("enrichment always emits — worst case an overlay");

        enriched.Should().NotBeNull();
        if (enriched!.HubConfiguration is null)
            return null;

        var applied = enriched.HubConfiguration(
            new MessageHubConfiguration(null, new Address("probe", typeName)));
        return applied.Get<UnhandledMessageNack>();
    }

    private Task CreateAsSystem(MeshNode node)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(node))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await();
    }
}
