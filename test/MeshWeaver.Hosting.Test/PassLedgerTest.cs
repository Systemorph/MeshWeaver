using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The shared per-pass outcome ledger the collection passes report through (prebuilt-bundle
/// retention, assembly-cache retention, modules GC): exactly ONE Warning <c>pass outcome</c> line
/// per pass, and a step boundary that turns the pool's cancel into an INTERRUPTED outcome plus an
/// <see cref="OperationCanceledException"/>.
/// </summary>
public class PassLedgerTest
{
    [Fact]
    public void AnOutcome_IsOneWarningLine_InTheSharedShape()
    {
        var log = new RecordingLogger();
        new PassLedger("SomePass", log).Record(PassOutcome.Collected, " {Files} file(s)", 3);

        log.Entries.Should().ContainSingle()
            .Which.Should().Be((LogLevel.Warning, "SomePass: pass outcome — COLLECTED 3 file(s)"));
    }

    [Fact]
    public void ASecondOutcome_ForOnePass_IsRefused()
    {
        var ledger = new PassLedger("SomePass", new RecordingLogger());
        ledger.Record(PassOutcome.ReportOnly, "");

        Assert.Throws<InvalidOperationException>(() => ledger.Record(PassOutcome.Collected, ""));
    }

    [Fact]
    public void AStepOnALiveToken_DoesNothing()
    {
        var log = new RecordingLogger();
        var ledger = new PassLedger("SomePass", log) { OnInterrupted = () => throw new InvalidOperationException("not interrupted") };

        ledger.Step(CancellationToken.None);

        log.Entries.Should().BeEmpty();
        ledger.Outcome.Should().BeNull();
    }

    [Fact]
    public void AStepOnACancelledToken_RecordsThePassesInterruption_ThenThrows()
    {
        var log = new RecordingLogger();
        var removed = 2;
        var ledger = new PassLedger("SomePass", log);
        ledger.OnInterrupted = () => ledger.Record(PassOutcome.Interrupted, ": after removing {Removed}", removed);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ledger.Step(cts.Token));
        // A second boundary (a catch that re-reports) adds no second line.
        Assert.ThrowsAny<OperationCanceledException>(() => ledger.Interrupt(cts.Token));

        log.Entries.Should().ContainSingle()
            .Which.Message.Should().Be("SomePass: pass outcome — INTERRUPTED: after removing 2");
    }

    [Fact]
    public void AnInterruptionThePassDidNotDescribe_IsStillReported()
    {
        var log = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new PassLedger("SomePass", log).Step(cts.Token));

        log.Entries.Should().ContainSingle().Which.Message.Should().StartWith("SomePass: pass outcome — INTERRUPTED");
    }

    [Fact]
    public void TheWrapper_ReportsACancelAsTeardown_AndAnythingElseAsFaulted()
    {
        var log = new RecordingLogger();

        PassLedger.ReportTerminal("SomePass", log, new OperationCanceledException(), ": over {Root}", "/r")
            .Should().BeTrue();
        PassLedger.ReportTerminal("SomePass", log, new System.IO.IOException("gone"), ": over {Root}", "/r")
            .Should().BeFalse();

        log.Entries.Select(e => e.Level).Should().Equal(LogLevel.Information, LogLevel.Warning);
        log.Entries.Last().Message.Should().Be("SomePass: pass outcome — FAULTED: over /r");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public (LogLevel Level, string Message)[] Entries => [.. entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
