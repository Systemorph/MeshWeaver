using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// REPRO for the 2-core "deadlock" (missed observation) where
/// <see cref="HubPermissionExtensions.GetEffectivePermissions(IMessageHub,string,string)"/>
/// for a NO-GRANT user never emits because the cold synced
/// <c>$security-access</c>/<c>$security-policy</c> queries (the <c>enriched</c> branch the
/// no-grant path depends on entirely — the synchronous seed is <c>Observable.Empty</c>) don't
/// deliver their initial snapshot through the cache's
/// <c>SubscribeOn(TaskPool)+Replay(1).AutoConnect(1)</c> composition under scheduler pressure.
///
/// A single no-grant read works (see GetEffectivePermissions_NoRoles_ReturnsNone). The bug only
/// surfaces under CONCURRENCY: many distinct cold queries subscribing at once on a constrained
/// scheduler. We HAMMER with distinct deep paths + distinct no-grant users so every read opens
/// fresh (uncached) cold queries up a multi-level scope hierarchy, then assert every read settles.
/// Run under <c>DOTNET_PROCESSOR_COUNT=2</c> to mirror CI. A hang here = the deadlock.
/// </summary>
public class PermissionQueryHammerReproTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 180_000)]
    public async Task NoGrantUsers_ConcurrentEffectivePermissions_AllSettle()
    {
        var ct = TestContext.Current.CancellationToken;
        const int n = 200;

        // Launch ALL reads concurrently — each opens fresh cold $security-access/$security-policy
        // queries (distinct scope per i, never cached) → maximal concurrent cold-subscribe pressure.
        var tasks = Enumerable.Range(0, n).Select(i =>
            Mesh.GetEffectivePermissions($"hammer{i}/space/sub/leaf{i}", $"nobody-{i}")
                .Take(1)
                .Timeout(60.Seconds())
                .ToTask(ct)).ToArray();

        Permission[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (TimeoutException)
        {
            var settled = tasks.Count(t => t.IsCompletedSuccessfully);
            throw new Xunit.Sdk.XunitException(
                $"DEADLOCK: only {settled}/{n} no-grant GetEffectivePermissions reads settled within 60s — " +
                $"the rest never emitted (cold synced-query missed observation under scheduler pressure).");
        }

        Assert.All(results, p => Assert.Equal(Permission.None, p));
        Output.WriteLine($"All {n} no-grant reads settled to None.");
    }
}
