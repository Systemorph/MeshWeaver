using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DISTRIBUTED (Orleans) repro for the atioz 2026-06-15 "composer disappears on select" wedge.
///
/// <para>When the <c>Harness</c> partition is DB-synced (the distributed/PG path), its built-in
/// catalog is materialized by <see cref="HarnessStaticRepoSource"/> and the in-memory provider that
/// would otherwise serve the catalog is gated OFF (<see cref="HarnessNodeType.AddHarnessType{TBuilder}"/>
/// with <c>serveFromPartition</c>). The bug: <c>HarnessStaticRepoSource</c> DROPPED the partition's
/// PublicRead <c>_Policy</c> (a <c>PartitionAccessPolicy</c> node), so the synced partition shipped
/// with NO read policy. Every user — even an admin, since partitions are not data-superuser readable —
/// was then DENIED Read on <c>Harness/MeshWeaver</c>; its node hub failed DataContext init with
/// <c>UnauthorizedAccessException</c> and went into a FAILED state, after which every GetData/Subscribe
/// to it deferred &gt;30s and failed. The chat composer's harness picker (which reads that node) could
/// no longer load, so the synced selectors area errored and the composer vanished.</para>
///
/// <para>This pins the contract end-to-end on a silo: after importing the real Harness static-repo
/// source, a REGULAR (non-admin, non-owner) user must be able to read <c>Harness/MeshWeaver</c>
/// because the catalog is PublicRead. RED before the fix (policy dropped → denied), GREEN after
/// (policy imported → PublicRead → readable).</para>
/// </summary>
public class OrleansHarnessPartitionPublicReadTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansHarnessPartitionPublicReadTest.HarnessSyncConfigurator>(output)
{
    private IServiceProvider SiloServices => ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
    private IMessageHub Mesh => SiloServices.GetRequiredService<IMessageHub>();
    private CancellationToken Ct => new CancellationTokenSource(55.Seconds()).Token;

    [Fact(Timeout = 90000)]
    public async Task SyncedHarnessPartition_IsPublicReadable_ByARegularUser_AfterImport()
    {
        var ct = Ct;

        // Materialize the Harness catalog into the silo partition — the synced/PG path.
        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);
        foreach (var r in results)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");

        var path = $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}";

        // (1) The PublicRead _Policy MUST have travelled to the DB partition (read under System).
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        MeshNode? policy;
        using (access.ImpersonateAsSystem())
            policy = await Mesh.GetWorkspace()
                .GetMeshNodeStream($"{HarnessNodeType.RootNamespace}/_Policy")
                .Where(n => n is not null)
                .Take(1).Timeout(20.Seconds())
                .Catch((Exception _) => Observable.Return<MeshNode?>(null))
                .FirstAsync().ToTask(ct);
        policy.Should().NotBeNull(
            "the synced Harness partition must import its PublicRead _Policy — without it the partition "
            + "has no read policy and Harness/MeshWeaver becomes unreadable");

        // (2) The wedge symptom: a REGULAR user (RLS enforced, not System) must be able to read the
        //     harness node. Before the fix this is denied → null → the hub init fails → FAILED.
        MeshNode? node;
        using (access.SwitchAccessContext(new AccessContext
        {
            ObjectId = "regular-user-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Regular User"
        }))
        {
            node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null)
                .Take(1).Timeout(20.Seconds())
                .Catch((Exception _) => Observable.Return<MeshNode?>(null))
                .FirstAsync().ToTask(ct);
        }

        node.Should().NotBeNull(
            $"a regular user must be able to read the PublicRead Harness catalog at '{path}'. "
            + "If null, the partition's _Policy was not imported and the read was denied — the exact "
            + "wedge that fails the composer's harness picker.");
    }

    /// <summary>Standard in-memory silo fixture + Space type + a DB-SYNCED Harness partition served
    /// by the real <see cref="HarnessStaticRepoSource"/> (in-memory provider gated off, as on prod).</summary>
    public class HarnessSyncConfigurator : TestSiloConfigurator
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            builder
                .AddSpaceType()
                // serveFromPartition={Harness} ⇒ DB-synced: the in-memory static surface is gated off,
                // exactly as the distributed portal wires it; the catalog must come from the import.
                .AddHarnessType(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { HarnessNodeType.RootNamespace })
                .ConfigureServices(s => s.AddSingleton<IStaticRepoSource>(sp =>
                    new HarnessStaticRepoSource(sp.GetRequiredService<BuiltInHarnessProvider>())));
    }
}
