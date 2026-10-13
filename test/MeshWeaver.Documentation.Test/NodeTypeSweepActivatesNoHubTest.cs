using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// #6623 — a GitSync sweep that reads every NodeType of a Space for its DEFINITION builds no
/// per-node hub, so the hub population after the sweep does not scale with the number of types.
///
/// <para>The production reading: the first replica of a generation held 1 059 node hubs half an hour
/// after boot, 403 of them two-segment addresses — a Space's NodeTypes. Both sweeps that read every
/// type's definition (<c>BundleKeyedHoldReading</c> on each import, <c>SealedPublicationSyncReconciler</c>
/// on each seal announcement and the boot pass) used <c>GetMeshNodeStream(path).Take(1)</c>, which
/// reaches the content by subscribing at the type's own address. They now share
/// <see cref="NodeTypeDefinitionReads.ReadDefinitions"/>, a batched store read.</para>
///
/// <para>🚨 The instrument is <see cref="HostedHubsCollection.HubAdded"/> — the build signal itself,
/// the collection <c>GetHostedHub(…, Never)</c> reads — never a registry count taken after a pause.
/// The NEGATIVE CONTROL runs the replaced read over the same types with the same instrument and must
/// see one hub per type; without it, "no hub was built" could be a stream that can never emit.</para>
/// </summary>
public class NodeTypeSweepActivatesNoHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Enough types that "scales with N" and "bounded" cannot be confused.</summary>
    private const int TypeCount = 12;

    /// <summary>The window the negative assertion spends in full: one local activation fits inside
    /// it many times over, CI-scaled like every other test bound.</summary>
    private static TimeSpan NothingIsBuiltWithin => TestTimeouts.Quick / 4;

    [Fact(Timeout = 120_000)]
    public async Task ReadingEveryDefinition_BuildsNoHub_WhileTheStreamReadBuildsOnePerType()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var paths = Enumerable.Range(0, TypeCount)
            .Select(i => $"{TestPartition}/Sweep{suffix}T{i}")
            .ToImmutableArray();

        foreach (var path in paths)
        {
            var id = path[(path.LastIndexOf('/') + 1)..];
            await access.RunAsSystem(() => meshService.CreateNode(new MeshNode(id, TestPartition)
                {
                    NodeType = MeshNode.NodeTypePath,
                    Name = id,
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition { Description = $"type {id}" },
                }))
                .Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"{path} must exist before the sweep reads it", ct);
        }

        var addresses = paths.Select(p => new Address(p)).ToImmutableHashSet();
        foreach (var address in addresses)
            Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
                "the precondition: creating a NodeType does not activate its hub, so any hub built "
                + "for these addresses below was built by the read under test");

        var hosted = Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>();

        // 1. THE FIX — the read both sweeps now use. Every definition comes back, and no hub is built.
        var builtBySweep = hosted.HubAdded
            .Where(h => addresses.Contains(h.Address with { Host = null }))
            .Replay();
        using (builtBySweep.Connect())
        {
            var definitions = await access
                .RunAsSystem(() => NodeTypeDefinitionReads.ReadDefinitions(Mesh, paths, logger: null))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the store answers the sweep's content read", ct);
            paths.Count(definitions.ContainsKey).Should().Be(TypeCount,
                "every listed type's definition is read — the bounded hub population is not bought "
                + "by reading fewer types");
            definitions[paths[0]].Description.Should().Be($"type Sweep{suffix}T0");

            await builtBySweep.Should().NotEmit(NothingIsBuiltWithin,
                "a sweep that needs only each type's CONTENT must not activate the type's per-node "
                + "hub — on a production replica that is one 4–5 MiB hub per NodeType of every "
                + "synced Space (#6623)",
                ct);
        }
        foreach (var address in addresses)
            Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
                "…and the registry the signal is fed from agrees: no type is hosted after the sweep");

        // 2. NEGATIVE CONTROL — the replaced read, same types, same instrument: one hub per type.
        var builtByStreamRead = hosted.HubAdded
            .Where(h => addresses.Contains(h.Address with { Host = null }))
            .Replay();
        using (builtByStreamRead.Connect())
        {
            await access.RunAsSystem(() => Observable.Zip(paths.Select(path =>
                        Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null).Take(1))))
                .Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the old per-type stream read answers too", ct);

            await builtByStreamRead.Take(TypeCount).ToList()
                .Should().Within(TestTimeouts.Convergence)
                .Emit("🚨 the old shape builds one hub PER TYPE — the population scales with N. This "
                      + "is the positive control for the NotEmit above: the same filter over the same "
                      + "signal does emit when a read activates", ct);
        }
        paths.Count(p => Mesh.GetHostedHub(new Address(p), HostedHubCreation.Never) is not null)
            .Should().Be(TypeCount, "the stream read leaves every type it read hosted");
    }
}
