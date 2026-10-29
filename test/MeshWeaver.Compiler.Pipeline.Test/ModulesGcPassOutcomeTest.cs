using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The modules GC pass reported its outcome at Information (and not at all when it removed
/// nothing), while the cluster ships only Warning+ to Loki — so whether a pass ran, what it
/// removed, and whether it refused were all invisible (#6991). Each pass now emits exactly ONE
/// Warning outcome line carrying its denominator; the per-directory lines stay Information.
/// </summary>
public class ModulesGcPassOutcomeTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-modules-gc-{Guid.NewGuid():N}");

    public ModulesGcPassOutcomeTest() => Directory.CreateDirectory(Path.Combine(root, "modules"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void APassThatRemoves_EmitsExactlyOneWarningOutcome()
    {
        Directory.CreateDirectory(Path.Combine(root, "modules", "Orphan@1.0.0"));
        Directory.CreateDirectory(Path.Combine(root, "modules", ".staging-abc"));
        var log = new OutcomeLogger();

        var removed = ModuleLandingService.CollectGarbage(
            root, log, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        removed.Should().Be(2);
        var warnings = log.Entries.Where(e => e.Level >= LogLevel.Warning).ToArray();
        warnings.Should().ContainSingle("one outcome line per pass is what keeps Warning+ affordable");
        warnings[0].Message.Should().Contain("pass outcome — COLLECTED")
            .And.Contain("removed 2 of 2 directory(ies)");
        log.Entries.Where(e => e.Message.Contains("Modules GC: removed"))
            .Should().OnlyContain(e => e.Level == LogLevel.Information, "per-directory lines stay Information");
    }

    [Fact]
    public void APassThatRemovesNothing_StillSaysItRan()
    {
        var log = new OutcomeLogger();

        ModuleLandingService.CollectGarbage(root, log, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("removed 0 of 0 directory(ies)",
                "silence must never be the only evidence that a pass removed nothing");
    }

    /// <summary>
    /// An unreadable reference record fails the pass CLOSED: the orphan survives, and the verdict
    /// travels on the ONE Warning outcome line with both counts — the explanatory "SKIPPING" line is
    /// Information. The read fault itself stays an Error (a corrupt record is a defect to triage,
    /// not a retention outcome). The `sets` metadata directory is counted apart from referenced
    /// generations.
    /// </summary>
    [Fact]
    public void AnUnreadableReference_FailsClosed_InTheOneWarningOutcome()
    {
        var orphan = Path.Combine(root, "modules", "Orphan@1.0.0");
        Directory.CreateDirectory(orphan);
        var sets = ModuleSetStore.SetsDirectory(root);
        Directory.CreateDirectory(sets);
        File.WriteAllText(Path.Combine(sets, "000000001-garbage000000000.proposed.json"), "{ not json");
        var log = new OutcomeLogger();

        var removed = ModuleLandingService.CollectGarbage(
            root, log, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        removed.Should().Be(0);
        Directory.Exists(orphan).Should().BeTrue("unreadable is never unreferenced");
        log.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("pass outcome — FAILED CLOSED")
            .And.Contain("1 unreferenced generation(s) NOT deleted")
            .And.Contain("kept 0 referenced generation(s), 1 non-generation director(ies)");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("SKIPPING"));
    }

    /// <summary>
    /// A superseded set record the ORDINARY prune cannot delete (duplicate pruning has nothing to
    /// do) is counted on the outcome line — it must never read "0 could not be read or removed"
    /// while a record refused deletion. Itemized at Information, never a second Warning.
    /// </summary>
    [Fact]
    public void AnOrdinaryPruneFailure_IsCountedOnTheOneWarningOutcome()
    {
        if (OperatingSystem.IsWindows())
            return; // the refusal below is a POSIX directory permission
        ModuleSetStore.Propose(root, Landed("A@1"), "replica-1").Should().NotBeNull();
        var current = ModuleSetStore.Propose(root, Landed("A@2"), "replica-1")!;
        ModuleSetStore.RecordAdoption(root, current, "replica-1");
        ModuleSetStore.Read(root).Current!.Sequence.Should().Be(2);
        var sets = ModuleSetStore.SetsDirectory(root);
        var mode = File.GetUnixFileMode(sets);
        File.SetUnixFileMode(sets, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var log = new OutcomeLogger();
        try
        {
            ModuleLandingService.CollectGarbage(root, log, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));
        }
        finally
        {
            File.SetUnixFileMode(sets, mode);
        }

        Directory.EnumerateFiles(sets, "000000001-*").Should().NotBeEmpty("the refused record is still there");
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("pass outcome — COLLECTED")
            .And.Contain("retired 0 module set record(s) (1 could not be read or removed)");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("could not be removed"));
    }

    /// <summary>
    /// The pool's cancel arriving mid-pass (#7049's shape, on this pass): the pass stops at the next
    /// directory, records ONE <c>INTERRUPTED</c> outcome carrying what it removed, and throws — it
    /// never walks on, and never reports the cut-short pass as COLLECTED. Before the shared
    /// <c>PassLedger</c> this pass logged <c>CANCELLED</c> and returned as if it had finished.
    /// </summary>
    [Fact]
    public void ACancelMidPass_StopsAtTheNextDirectory_WithOneInterruptedOutcome()
    {
        var orphans = new[] { "A@1.0.0", "B@1.0.0", "C@1.0.0" }
            .Select(name => Path.Combine(root, "modules", name)).ToArray();
        foreach (var orphan in orphans)
            Directory.CreateDirectory(orphan);
        using var cts = new CancellationTokenSource();
        // The drain arrives the moment the first directory is removed.
        var log = new OutcomeLogger(message =>
        {
            if (message.StartsWith("Modules GC: removed", StringComparison.Ordinal))
                cts.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => ModuleLandingService.CollectGarbage(
            root, log, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1), cancellationToken: cts.Token));

        orphans.Count(Directory.Exists).Should().Be(2, "the pass stops at the next directory; the rest is a later pass's");
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle(
                "an interrupted pass reports exactly one outcome, never INTERRUPTED and COLLECTED both")
            .Which.Message.Should().Contain("pass outcome — INTERRUPTED")
            .And.Contain("removed 1 of 1 directory(ies)");
    }

    private static ModuleActivationList Landed(string generation) => new()
    {
        Entries = [new ModuleActivationEntry { Name = "A", Directory = generation }],
    };

    /// <summary>
    /// #7349 — a base directory with no <c>modules/</c> folder used to return before any outcome,
    /// so that pass was indistinguishable from one that never ran. It now reports SKIPPED, once.
    /// </summary>
    [Fact]
    public void APassWithNoModulesFolder_ReportsSkipped_Once()
    {
        var bare = Path.Combine(root, "no-modules-here");
        Directory.CreateDirectory(bare);
        var log = new OutcomeLogger();

        var removed = ModuleLandingService.CollectGarbage(bare, log, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow);

        removed.Should().Be(0);
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle(
                "a pass with nothing to sweep still says it ran")
            .Which.Message.Should().Contain("pass outcome — SKIPPED").And.Contain("modules");
    }

    private sealed class OutcomeLogger(Action<string>? onLog = null) : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue((logLevel, message));
            onLog?.Invoke(message);
        }
    }
}
