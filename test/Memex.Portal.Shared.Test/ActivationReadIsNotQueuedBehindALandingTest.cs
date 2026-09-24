using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#4963 / #4804 — a read of the module-activation record must never queue behind a
/// LANDING in flight.
///
/// <para>The registry answers every <c>/api/plugins/bundles/index.json</c> with two reads of the
/// activation record and every bundle download with one, and those reads ran on the same cap-1
/// pool that serialises <see cref="ModuleLandingService.ShelveModule"/> — the publish landing, one
/// SMB round trip per file on the shared volume. So a consumer's authenticated index request waited,
/// with nothing written to it, for every landing in flight and every other request's read, one at a
/// time, process-wide: #4963's <i>"0 bytes received"</i> for a whole client budget, from a host
/// answering every other probe in ~0.1 s.</para>
///
/// <para>Deterministic, not timed: the landing is PARKED inside its recording window (after its
/// bytes are on the volume, before anything is recorded — the #4026 seam), which holds the landing
/// lane for as long as the test wants. The read is then asked for with a bound far shorter than the
/// park. On the old lane the read cannot run until the park ends, so it fails; on its own lane it
/// answers at once.</para>
/// </summary>
public class ActivationReadIsNotQueuedBehindALandingTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.ReadBesideALanding";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-read-beside-landing-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root.</summary>
    public ActivationReadIsNotQueuedBehindALandingTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>
    /// A landing parked in its recording window holds the landing lane; the activation read still
    /// answers — and answers what is TRUE at that moment (nothing recorded yet), then, once the
    /// landing finishes, the landed head.
    /// </summary>
    [Fact]
    public async Task AReadAnswersWhileALandingHoldsTheLandingLane()
    {
        var ct = TestContext.Current.CancellationToken;
        var released = 0;
        var parked = new AsyncSubject<Unit>();
        using var landing = new ModuleLandingService(null, root, beforeRecording: _ =>
        {
            parked.OnNext(Unit.Default);
            parked.OnCompleted();
            // The deliberate park — the subject of this test. Bounded, and released in the finally
            // below, so a failing assertion can never strand the landing's thread.
            SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TestTimeouts.Convergence);
        });

        var landed = new ReplaySubject<ModuleLandingOutcome>();
        using var landingSubscription = landing.ShelveModule(
                Module,
                [(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                frameworkMvid: "test-build", packagePath: "Plugins/ReadBesideALanding", version: "1.0.0",
                staticAssets: [("wwwroot/build.txt", Encoding.UTF8.GetBytes("1.0.0"))])
            .Subscribe(landed);
        try
        {
            await parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the landing must reach its recording window, or nothing below is measured",
                    cancellationToken: ct);

            // 🚨 THE ASSERTION. The landing lane is held until `released` flips; the read must not
            // need it. Quick is a third of the park's bound, so on the old lane this cannot pass.
            var during = await landing.GetActivation().Should().Within(TestTimeouts.Quick)
                .Emit("a read of the activation record must not wait for a landing in flight — "
                      + "the registry's bundle index reads it on every request (#4963)",
                    cancellationToken: ct);

            // And the read is not a torn one: the parked landing has put bytes on the volume but
            // recorded nothing, so the module is not yet an entry.
            Assert.DoesNotContain(during.Entries,
                e => string.Equals(e.Name, Module, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Volatile.Write(ref released, 1);
        }

        // Positive control: the landing itself completes, and a read after it sees the head.
        await landed.Should().Within(TestTimeouts.Convergence)
            .Emit("the parked landing must complete once released", cancellationToken: ct);
        var after = await landing.GetActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the activation read must answer after the landing", cancellationToken: ct);
        var head = Assert.Single(after.Entries,
            e => string.Equals(e.Name, Module, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1.0.0", head.Version);
    }
}
