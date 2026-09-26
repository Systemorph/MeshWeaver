using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#4963 — the registry's bundle index must read a MAINTAINED activation list, not
/// derive it from the volume on every request.
///
/// <para>Measured on the fleet registry (memex.meshweaver.cloud, 2026-09-26): one derivation of
/// the activation list over its 44 modules on the Azure Files share took 7.3 s and 8.7 s, and
/// <c>/api/plugins/bundles/index.json</c> ran it TWICE per request — ~16 s of the 17.8 s a
/// consumer measured for one index read, for a list that had not changed between any two
/// requests. <see cref="ModuleLandingService.GetServedActivation"/> is the index's reader now.</para>
///
/// <para>Deterministic: two landing services on one root stand in for two replicas on the shared
/// volume (the #4026 shape), and the freshness bound is measured on a test clock the test moves.
/// "Not re-derived" is observed as what a re-derivation could not have answered: a record another
/// replica wrote is ON the volume (the authoritative read sees it) and the served read does not
/// report it until the snapshot is due.</para>
/// </summary>
public class ServedActivationIsMaintainedNotScannedTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.ServedActivation";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-served-activation-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the shared landing root.</summary>
    public ServedActivationIsMaintainedNotScannedTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>
    /// A served read answers from the snapshot: a landing another replica recorded since is on
    /// the volume, and the served read does not rescan to find it while the snapshot is fresh.
    /// </summary>
    [Fact]
    public async Task AServedReadDoesNotRescanTheVolume()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestClock();
        using var registry = new ModuleLandingService(baseDirectory: root) { ServedActivationClock = clock };
        using var otherReplica = new ModuleLandingService(baseDirectory: root);

        var primed = await registry.GetServedActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the first served read derives the list", cancellationToken: ct);
        Assert.DoesNotContain(primed.Entries, IsTheModule);

        await Shelve(otherReplica, "1.0.0").Should().Within(TestTimeouts.Convergence)
            .Emit("the other replica's landing must complete", cancellationToken: ct);

        // Positive control: the landing IS on the volume, so a derivation would report it.
        var authoritative = await registry.GetActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the authoritative read derives the list", cancellationToken: ct);
        Assert.Contains(authoritative.Entries, IsTheModule);

        // 🚨 THE ASSERTION. Twice, because the index used to read twice per request.
        for (var i = 0; i < 2; i++)
        {
            var served = await registry.GetServedActivation().Should().Within(TestTimeouts.Quick)
                .Emit("a served read answers from the maintained snapshot", cancellationToken: ct);
            Assert.DoesNotContain(served.Entries, IsTheModule);
        }
    }

    /// <summary>
    /// A landing THIS replica makes invalidates its snapshot before it is announced, so the replica
    /// that took a publish serves it on the very next read — no freshness wait.
    /// </summary>
    [Fact]
    public async Task ThisReplicasOwnLandingIsServedAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestClock();
        using var registry = new ModuleLandingService(baseDirectory: root) { ServedActivationClock = clock };

        var primed = await registry.GetServedActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the first served read derives the list", cancellationToken: ct);
        Assert.DoesNotContain(primed.Entries, IsTheModule);

        await Shelve(registry, "1.0.0").Should().Within(TestTimeouts.Convergence)
            .Emit("the landing must complete", cancellationToken: ct);

        var served = await registry.GetServedActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the served read after this replica's own landing", cancellationToken: ct);
        var head = Assert.Single(served.Entries, IsTheModule);
        Assert.Equal("1.0.0", head.Version);
    }

    /// <summary>
    /// Another replica's landing reaches the served list once the snapshot is due: the first read
    /// past the bound still answers at once (from the snapshot) and starts the one re-derivation;
    /// the list then carries the landing.
    /// </summary>
    [Fact]
    public async Task AnotherReplicasLandingIsServedOnceTheSnapshotIsDue()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestClock();
        using var registry = new ModuleLandingService(baseDirectory: root) { ServedActivationClock = clock };
        using var otherReplica = new ModuleLandingService(baseDirectory: root);

        await registry.GetServedActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the first served read derives the list", cancellationToken: ct);
        await Shelve(otherReplica, "1.0.0").Should().Within(TestTimeouts.Convergence)
            .Emit("the other replica's landing must complete", cancellationToken: ct);

        clock.Advance(registry.ServedActivationFreshness);

        var stale = await registry.GetServedActivation().Should().Within(TestTimeouts.Quick)
            .Emit("a due snapshot still answers at once — revalidation runs beside the answer",
                cancellationToken: ct);
        Assert.DoesNotContain(stale.Entries, IsTheModule);

        // The re-derivation the due read started lands in the snapshot; wait on that condition,
        // re-reading the served list (a request/response source), never on a sleep.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => registry.GetServedActivation())
            .Where(list => list.Entries.Any(IsTheModule))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the re-derivation the due read started must reach the served list",
                cancellationToken: ct);
    }

    private static bool IsTheModule(ModuleActivationEntry entry) =>
        string.Equals(entry.Name, Module, StringComparison.OrdinalIgnoreCase);

    private static IObservable<ModuleLandingOutcome> Shelve(ModuleLandingService landing, string version) =>
        landing.ShelveModule(
            Module,
            [(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
            frameworkMvid: "test-build", packagePath: "Plugins/ServedActivation", version: version);

    /// <summary>A clock the test moves — the freshness bound is measured on it.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
