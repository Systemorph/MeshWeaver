using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Mesh.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#8438 — <b>one slow reader of the feature flags never stalls another.</b>
///
/// <para><b>What was observed.</b> A portal's default-install repair pass logged
/// <c>[DefaultInstall] the environment's feature flags could not be read … TimeoutException</c>
/// exactly 5.006 s after the pass started, while the process heartbeat in the same seconds showed no
/// GC pause and no thread-pool backlog. The reader was a <see cref="System.Reactive.Subjects.BehaviorSubject{T}"/>,
/// which delivers its current value to a new subscriber while holding its own lock, so any OTHER
/// subscriber whose synchronous first-value downstream was still running held every new reader out
/// of <c>Subscribe</c> — and the install pass's 5-second timer, armed before it subscribed, fired.</para>
///
/// <para><b>Both directions.</b> The fix case parks one subscriber inside its first delivery and
/// expects a second reader on another thread to get the reading anyway. The control proves the
/// replay is still synchronous when nothing contends — the property every existing reader relies
/// on (<see cref="DeclaredDisabledFlagCompositionTest.Composed"/> reads it with no blocking bridge).</para>
/// </summary>
public class FeatureFlagReplayOutsideTheLockTest
{
    private static ConfigurationFeatureFlags Flags() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Flags:games:Enabled"] = "false",
            ["Features:Flags:games:Packages:0"] = "Plugins/Games",
        })
        .Build());

    [Fact]
    public void ASubscriberParkedInItsFirstDelivery_DoesNotHoldAnotherReaderOut()
    {
        using var flags = Flags();
        // The release INTO the parked holder is a volatile int it polls under a bounded spin,
        // written in the finally, so a red run never strands it (HandWovenGateRatchetGuard).
        var holderInside = 0;
        var release = 0;
        var holderDone = 0;
        var holder = new Thread(() =>
        {
            try
            {
                using (flags.All.Take(1).Subscribe(_ =>
                {
                    Volatile.Write(ref holderInside, 1);
                    SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(15));
                }))
                {
                }
            }
            finally { Volatile.Write(ref holderDone, 1); }
        }) { IsBackground = true, Name = "flags-holder" };

        FeatureComposition? read = null;
        TimeSpan readTook;
        holder.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref holderInside) == 1, TimeSpan.FromSeconds(10)),
                "the holder never received its first delivery");

            // The reader runs on THIS thread: with the replay under the subject's lock it would sit in
            // Subscribe until the holder's spin budget ran out.
            var clock = Stopwatch.StartNew();
            using (flags.Composition.Take(1).Subscribe(c => read = c)) { }
            readTook = clock.Elapsed;
        }
        finally
        {
            Volatile.Write(ref release, 1);
            SpinWait.SpinUntil(() => Volatile.Read(ref holderDone) == 1, TimeSpan.FromSeconds(20));
        }

        Assert.NotNull(read);
        Assert.True(readTook < TimeSpan.FromSeconds(5),
            $"a reader must get the flags while another subscriber is still inside its first delivery; it took "
            + $"{readTook.TotalSeconds:F1} s — a BehaviorSubject held it out of Subscribe until the holder returned (#8438)");
        Assert.Contains(read.Excluded, p => p.Package == "Plugins/Games");
    }

    [Fact]
    public void Uncontended_TheReadingArrivesInsideSubscribe()
    {
        using var flags = Flags();
        FeatureComposition? read = null;

        using (flags.Composition.Take(1).Subscribe(c => read = c)) { }

        Assert.NotNull(read);
        Assert.Contains(read.Retired, p => p.Package == "Plugins/Games");
    }

    [Fact]
    public void AReload_ReachesAnOpenSubscriber_AfterItsFirstReading()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Flags:beta"] = "true" })
            .Build();
        using var flags = new ConfigurationFeatureFlags(configuration);
        var seen = new List<bool>();

        using (flags.IsEnabled("beta").Subscribe(seen.Add))
        {
            configuration["Features:Flags:beta"] = "false";
            configuration.Reload();
        }

        Assert.Equal([true, false], seen);
    }
}
