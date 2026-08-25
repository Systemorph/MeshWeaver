using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using Microsoft.Extensions.Logging;
using Xunit;

#pragma warning disable CS1591

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The adopt-only import mode on the install parse lane (MeshWeaver#2193 §B): with
/// <c>Modules:ImportSourceNodes=false</c>, compile-input files (<c>Source/</c>, <c>Test/</c>) are
/// not node candidates AT ALL — not parsed, not persisted, and not counted as parse-failure skips
/// (the policy is reported once, as information with a count, never as warning noise). With the
/// flag on — the default — behaviour is unchanged: the built-in C# parser turns compile inputs
/// into Code nodes at their canonical paths, exactly as today.
/// </summary>
public class ImportSourceNodesTest
{
    // The registry with its BUILT-IN parsers only (no extras injected) — .cs files parse into
    // Code nodes by default, which is exactly what the flag-off mode must prevent.
    private static readonly FileFormatParserRegistry Parsers =
        new(new JsonSerializerOptions(JsonSerializerDefaults.Web), []);

    private static readonly IReadOnlyList<PackageFile> Files =
    [
        new("README.md", "# display file — never a node"),
        new("Store/Plugin/Source/PluginContent.cs", "// compile input"),
        new("Store/Plugin/Test/PluginCoverTests.cs", "// compile input"),
    ];

    /// <summary>Records every log entry, so the two modes are distinguishable by what they SAY.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void FlagOff_CompileInputsAreNotCandidates_ReportedAsPolicyNotAsSkips()
    {
        var log = new RecordingLogger();
        var nodes = PackageInstaller.ParseAll(
            Parsers, Files, "Store", log, importSourceNodes: false);

        Assert.Empty(nodes);
        // The policy is one INFORMATION line with the count…
        var info = Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("2 compile-input file(s)", info.Message);
        Assert.Contains("Modules:ImportSourceNodes", info.Message);
        // …and nothing reached the parser, so there is no "had no parser" warning at all.
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void FlagOn_TheDefault_CompileInputsBecomeCodeNodes_ExactlyAsToday()
    {
        var log = new RecordingLogger();
        var nodes = PackageInstaller.ParseAll(Parsers, Files, "Store", log);

        // Same files, default mode: the registry's built-in C# parser turns both compile inputs
        // into Code nodes at their canonical paths — today's behaviour, byte for byte. The pin is
        // the CONTRAST with the flag-off case above: same inputs, different candidacy.
        Assert.Equal(2, nodes.Length);
        Assert.All(nodes, n => Assert.Equal("Code", n.NodeType));
        Assert.Contains(nodes, n => n.Path == "Store/Plugin/Source/PluginContent");
        Assert.Contains(nodes, n => n.Path == "Store/Plugin/Test/PluginCoverTests");
        Assert.Empty(log.Entries);
    }
}
