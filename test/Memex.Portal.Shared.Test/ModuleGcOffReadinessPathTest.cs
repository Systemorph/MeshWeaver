using System.Reactive.Linq;
using Memex.Portal.Shared;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #2684, pinned: the portal boot path must NEVER run the <c>modules/</c> generations GC before
/// the host listens. On an Azure Files (CIFS) <c>/data</c> the rename-then-recursive-delete of
/// orphaned generations is one SMB round-trip per file — minutes of uninterruptible IO — and it
/// reclaims nothing the portal needs in order to SERVE. When it ran synchronously in
/// <c>ConfigureMemexMesh</c>, rollout time became a function of how much garbage the previous
/// generation left on a network volume: memex-cloud's roll to ci.6559 sat as PID 1 in <c>Dsl</c>
/// deleting a <c>.trash-*</c> generation, never bound :8080, blew the 300 s startup probe (whose
/// kill cannot land on a process parked in uninterruptible IO), and looped.
///
/// <para>This test drives the REAL boot path (<c>ConfigureMemexMesh</c>) over a module root that
/// contains a genuine orphan generation and asserts the three phases in order: configuring the
/// mesh does not collect it, starting the hosted services (everything that happens before the
/// listener is up) does not collect it, and only <c>ApplicationStarted</c> — the signal that fires
/// strictly after the host listens — triggers the pass that does. Reclaim is deferred, never
/// dropped: the orphan IS collected afterwards.</para>
/// </summary>
public class ModuleGcOffReadinessPathTest
{
    [Fact]
    public async Task TheBootPathDefersGenerationsGc_UntilTheHostListens_ThenStillReclaims()
    {
        var root = Directory.CreateTempSubdirectory("mw-2684-").FullName;
        try
        {
            // A genuine orphan: a generation directory no activation entry references, backdated
            // past the #2303 grace window so only the WHEN of the pass — not the grace period —
            // decides its fate.
            var modules = Path.Combine(root, "modules");
            var orphan = Path.Combine(modules, "MeshWeaver.Widget@dead0001");
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(orphan, "MeshWeaver.Widget.dll"), "ORPHAN");
            Directory.SetLastWriteTimeUtc(orphan,
                DateTime.UtcNow - ModuleLandingService.DefaultGarbageMinAge - TimeSpan.FromMinutes(1));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Graph:Storage:Type"] = "FileSystem",
                    ["Graph:Storage:BasePath"] = root,
                    ["Modules:Root"] = root,
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            // The REAL host-lifetime implementation (the generic host's own), so ApplicationStarted
            // has production semantics: registered callbacks run when NotifyStarted fires — which
            // the host does strictly AFTER every hosted service (the listener included) started.
            var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
            services.AddSingleton<IHostApplicationLifetime>(lifetime);

            new MeshBuilder(configure => configure(services), new Address("mesh", "test"))
                .ConfigureMemexMesh(configuration);

            // Phase 1 — the boot path itself. This is where the synchronous CollectGarbage call
            // used to live; the orphan surviving it is the fix.
            Assert.True(Directory.Exists(orphan),
                "ConfigureMemexMesh must not collect garbage synchronously — that pass on a CIFS "
                + "/data is what blew the 300 s startup probe (#2684)");

            await using var provider = services.BuildServiceProvider();
            var gc = provider.GetRequiredService<ModuleGenerationsGcHostedService>();

            // Phase 2 — hosted-service StartAsync: everything that runs BEFORE the host listens.
            await gc.StartAsync(CancellationToken.None);
            Assert.True(Directory.Exists(orphan),
                "StartAsync runs before the listener is bound, so it must only register the "
                + "ApplicationStarted callback — never sweep");

            // Phase 3 — the host is listening. Reclaim is deferred, not dropped.
            lifetime.NotifyStarted();
            var removed = await gc.Completed
                .Timeout(TimeSpan.FromSeconds(30))
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(orphan),
                "the post-start pass must still reclaim the orphan — off the readiness path is "
                + "not off the roster");
            Assert.Empty(Directory.EnumerateDirectories(modules, ".trash-*"));

            await gc.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }
}
