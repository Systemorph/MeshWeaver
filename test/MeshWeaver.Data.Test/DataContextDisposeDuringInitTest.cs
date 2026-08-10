using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the recognized-shutdown outcome for the <see cref="DataContext"/> initialization
/// watchdog (issue #1122, the <c>$model-probe</c> 120s post-mortem timeout).
///
/// <para><b>The production sequence.</b> A transient <c>$model-probe/{guid}</c> hub arms the
/// DataContext init watchdog (<c>Task.WhenAny(allInit, Task.Delay(120s))</c>) the moment its
/// gate opens. When its data sources cannot initialize (upstream hub cache unavailable) the
/// probe's caller times out after 30s and disposes the probe — by design; that IS the probe
/// failing legibly. But the watchdog delay was uncancellable: 90 seconds AFTER the hub was
/// disposed it fired anyway and stamped a fail-level "DataContext initialization TIMED OUT …
/// Hub is now in FAILED state." (plus InitializationError, a global rejection handler, and
/// OnError'd data streams) onto a hub that no longer existed.</para>
///
/// <para><b>The fix.</b> <see cref="DataContext.Dispose"/> (which only runs during hub
/// teardown) cancels the watchdog delay, and the watchdog continuation treats
/// <see cref="IMessageHub.IsShuttingDown"/> as a recognized shutdown: Debug log, no
/// <see cref="DataContext.InitializationError"/>, no post-mortem residue.</para>
///
/// <para><b>RED before the fix:</b> after disposing the hub mid-init, the watchdog still fires
/// at its deadline and sets <c>InitializationError</c>. <b>GREEN after:</b> it stays null.
/// A hub that is NOT disposing keeps the existing terminal-FAILED treatment for a hung init
/// (the 2026-06-26 prod-wedge guard) — that branch is untouched.</para>
/// </summary>
public class DataContextDisposeDuringInitTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record HangingItem(string Id);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration.AddData(data => data
            // Short bound so the (pre-fix) post-mortem firing is observable fast; prod is 120s.
            .WithInitializationTimeout(TimeSpan.FromMilliseconds(1500))
            .AddSource(src => src.WithType<HangingItem>(t => t
                .WithKey(i => i.Id)
                // A data source whose initial load NEVER completes — the probe's "data source
                // that never initialised" (upstream cache unavailable).
                .WithInitialData(() => Observable.Never<IEnumerable<HangingItem>>()))));

    [Fact(Timeout = 30000)]
    public async Task InitWatchdog_HubDisposedMidInit_LeavesNoFailedResidue()
    {
        var host = GetHost();
        // Capture before disposal — the DataContext outlives the hub as a plain object, which
        // is exactly how the pre-fix watchdog could still write to it post-mortem.
        var dataContext = host.GetWorkspace().DataContext;

        // Dispose while the data source is still initializing — the transient-probe lifecycle
        // ($model-probe is created, read once, disposed; dispose-during-init is a NORMAL path).
        host.Dispose();
        await host.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask();

        // Sanctioned negative wait (WritingTests.md: "wait to confirm nothing happened"): run
        // PAST the watchdog deadline to prove it does not fire post-mortem. There is no
        // positive signal to filter for — the fix is precisely that nothing happens.
        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        dataContext.InitializationError.Should().BeNull(
            "a hub disposed while its data sources were still initializing ended by SHUTDOWN, "
            + "not by timeout — the watchdog must not stamp FAILED-state residue (fail-level "
            + "log, InitializationError, rejection handler) onto a disposed hub");
    }
}
