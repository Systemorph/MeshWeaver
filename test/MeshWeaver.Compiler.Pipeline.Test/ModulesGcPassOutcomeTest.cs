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
        warnings[0].Message.Should().Contain("pass outcome — completed")
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

    private sealed class OutcomeLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
