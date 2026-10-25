#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 An admin-record catalog's "has it listed?" signal must never run a subscriber's code under the
/// lock its own <see cref="InstanceAdminRecordCatalog{TContent}.IsLoaded"/> takes (Doc/Architecture/CatalogLoadedSignal).
///
/// <para>The measured deadlock (MeshWeaver.Plugins <c>ModuleAutoDiscoveryTest</c>, one test per class
/// run, stacks taken during the hang): <c>InstanceGitHubApp.WhenReadable</c> zips the secret catalog's
/// <c>WhenLoaded</c> with the App catalog's, and its consumer (<c>GitHubSyncService.DecideRead</c>)
/// asks <c>GitHubAppTokenService.IsConfigured</c> → <c>InstanceGitHubApp.Options</c> → the App
/// catalog's <c>IsLoaded</c>. With the signal a <c>BehaviorSubject&lt;bool&gt;</c>:</para>
/// <list type="bullet">
/// <item>thread A subscribes the zip; the App catalog's subject REPLAYS <c>true</c> while holding its
/// gate, and the <c>Take(1)</c> behind it completes into the zip — which wants the zip's gate;</item>
/// <item>thread B is the secret catalog's first listing: it pushes into the zip, which emits under
/// its gate into the consumer, which reads <c>IsLoaded</c> — <c>BehaviorSubject.Value</c>, the gate
/// thread A holds.</item>
/// </list>
/// <para>Neither ever returns; the module scan behind it emitted nothing for 240 s. This test pins the
/// one edge that closes the cycle, deterministically: a <c>WhenLoaded</c> subscriber parks inside its
/// callback until another thread has read <c>IsLoaded</c>. Under the old signal that read blocks on the
/// gate the parked callback holds, so it never completes within the park.</para>
/// </summary>
public class CatalogLoadedSignalLockOrderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(InstanceAdminRecords.Partition) { Name = "Admin", NodeType = "Markdown" })
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());
            });

    [Fact]
    public async Task IsLoaded_ReadsWhileAWhenLoadedSubscriberIsStillRunning()
    {
        var apps = Mesh.ServiceProvider.GetRequiredService<InstanceGitHubAppCatalog>();
        await apps.WhenLoaded.Should().Within(TestTimeouts.Convergence)
            .Emit("the catalog's first listing of the Admin partition arrives", TestContext.Current.CancellationToken);

        var callbackEntered = 0;
        var readCompleted = 0;
        var readSeenByCallback = 0;
        var readValue = 0;

        // The second thread is the zip's other producer in the measured deadlock: it reads IsLoaded
        // while a WhenLoaded subscriber is mid-callback on the first.
        var reader = new Thread(() =>
        {
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref callbackEntered) == 1, TimeSpan.FromSeconds(30)))
                return;
            Volatile.Write(ref readValue, apps.IsLoaded ? 1 : 2);
            Volatile.Write(ref readCompleted, 1);
        }) { IsBackground = true, Name = "IsLoaded reader" };
        reader.Start();

        try
        {
            using var subscription = apps.WhenLoaded.Subscribe(_ =>
            {
                Volatile.Write(ref callbackEntered, 1);
                // Parked until the other thread's read has RETURNED. A read that needs a lock this
                // callback is running under cannot return while it is parked.
                if (SpinWait.SpinUntil(() => Volatile.Read(ref readCompleted) == 1, TimeSpan.FromSeconds(5)))
                    Volatile.Write(ref readSeenByCallback, 1);
            });
        }
        finally
        {
            // Never strand the reader, whatever the assertion below decides.
            Volatile.Write(ref callbackEntered, 1);
        }

        Volatile.Read(ref readSeenByCallback).Should().Be(1,
            "IsLoaded must be readable from another thread while a WhenLoaded subscriber's callback runs — "
            + "a signal that replays under the lock IsLoaded takes is half of a lock-order deadlock "
            + "(the zip in InstanceGitHubApp.WhenReadable is the other half)");
        Volatile.Read(ref readValue).Should().Be(1, "the catalog has listed, so IsLoaded is true");
    }
}
