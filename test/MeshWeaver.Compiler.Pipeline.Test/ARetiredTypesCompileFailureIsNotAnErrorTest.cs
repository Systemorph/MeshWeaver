using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🔴 <b>A RETIRED type's compile failure is not incident-grade — Systemorph/MeshWeaver#5219.</b>
///
/// <para>When a repository stops carrying a NodeType whose instances still exist, the import HOLDS
/// the type and stamps <c>NodeTypeDefinition.PendingRetirement</c>. Its sources then reference
/// symbols that are gone, so its compile fails by design, and the bake gate classifies that
/// failure as <c>Retired</c>: "not a regression". The compile funnel did not ask the same
/// question. It logged the failure at <c>Error</c> before and regardless of that verdict, so
/// the red-log watcher kept incident <c>0e19a398d30973fc</c> alive. On memex.systemorph.com,
/// <c>Crm/Client</c> failed this way on every pod boot (2,206 occurrences by 2026-10-08) while
/// that instance's own <c>/health</c> read "1 retired by their repository — Crm/Client".</para>
///
/// <para>These tests pin one rule: the funnel's level and the bake gate's verdict use the same
/// predicate. The negative control is a type with no stamp, whose failure must stay
/// <c>Error</c>.</para>
/// </summary>
public class ARetiredTypesCompileFailureIsNotAnErrorTest
{
    /// <summary>The stamp shape <c>NodeTypeInstanceProbe.PendingRetirementOf</c> writes.</summary>
    private const string Stamp =
        "retired by Systemorph/MeshWeaver.Crm sync at 2026-09-24T16:29:03Z; held for 7 instance(s): "
        + "ExampleRe, SchenkerLabs, …";

    /// <summary>The live failure: the retired type's sources name the types Phase C deleted.</summary>
    private static CompilationException CrmClientFailure() => new(
        "Crm/Client",
        "Compilation failed for 'Crm/Client':\n"
        + "CS0246 Error (line 8073): The type or namespace name 'ClientContent' could not be found\n"
        + "CS0117 Error (line 459): 'CrmQueries' does not contain a definition for 'ClientType'");

    private static readonly IReadOnlyList<string> Queries = ["Crm/Client/Source scope:descendants"];
    private static readonly IReadOnlyList<string> Matched = ["Crm/Client/Source/ClientLayoutAreas"];

    private static NodeTypeDefinition Definition(string? pendingRetirement) => new()
    {
        PendingRetirement = pendingRetirement,
        LastCompileSucceededAt = DateTimeOffset.Parse("2026-09-15T00:00:00Z"),
        CurrentSourceVersions = ImmutableDictionary<string, long>.Empty
            .Add("Crm/Client/Source/ClientLayoutAreas", 12),
    };

    [Fact]
    public void ARetiredTypesFailure_IsReportedOnce_AtWarning_AndSaysWhy()
    {
        var logger = new RecordingLogger();

        CompileDiagnostics.ReportCompileFailure(
            logger, CrmClientFailure(), "Crm/Client", Stamp, Queries, Matched);

        var line = logger.Records.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning,
            "the repository withdrew this type on purpose and the bake gate already reads its "
            + "failure as Retired, not a regression; at Error the red-log watcher re-ticketed it on "
            + "every pod boot (#5219)");
        line.Message.Should().Contain("Retired by its repository")
            .And.Contain(Stamp, "the line names who retired it and which instances keep it alive")
            .And.Contain("CS0246", "the compiler's verdict is still in the report, not dropped");
    }

    [Fact]
    public void NegativeControl_AnUnstampedTypesFailure_StaysAnError()
    {
        var logger = new RecordingLogger();

        CompileDiagnostics.ReportCompileFailure(
            logger, CrmClientFailure(), "Crm/Client", pendingRetirement: null, Queries, Matched);

        var line = logger.Records.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error,
            "a type its repository still carries that fails to compile is a content defect and must "
            + "keep reaching the incident pipeline; only a RETIRED type's failure is expected");
        line.Message.Should().NotContain("Retired by its repository");
    }

    [Theory]
    [InlineData(Stamp, PreWarmStatus.Retired, LogLevel.Warning)]
    [InlineData(null, PreWarmStatus.CompileError, LogLevel.Error)]
    [InlineData("", PreWarmStatus.CompileError, LogLevel.Error)]
    public void TheFunnelsLevel_AgreesWithTheBakeGatesVerdict(
        string? stamp, PreWarmStatus expectedVerdict, LogLevel expectedLevel)
    {
        var definition = Definition(stamp);

        DynamicTypePreWarmer.ClassifyCompileFailure(definition, "Crm/Client")
            .Should().Be(expectedVerdict, "the bake gate's classification is the reference");
        CompileDiagnostics.ReportCompileFailure(
                new RecordingLogger(), CrmClientFailure(), "Crm/Client",
                definition.PendingRetirement, Queries, Matched)
            .Should().Be(expectedLevel,
                "the funnel and the bake gate must answer 'is this a regression?' the same way. "
                + "A funnel that disagrees is how an expected failure kept an incident open for weeks");
    }

    /// <summary>
    /// 🚨 <b>The compile service reports through <see cref="CompileDiagnostics.ReportCompileFailure"/>
    /// and nothing else.</b> A funnel that went back to a direct <c>LogError</c> would pass every
    /// test above while ticketing retired types again. This guard is what fails if that happens.
    /// </summary>
    [Fact]
    public void TheCompileServiceReportsFailuresOnlyThroughTheClassifyingReporter()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var service = File.ReadAllText(
            Path.Combine(src, "MeshWeaver.Compiler.Pipeline", "MeshNodeCompilationService.cs"));

        Regex.Matches(service, @"CompileDiagnostics\.ReportCompileFailure\(").Count.Should().Be(1,
            "the single compile-failure funnel reports through the reporter that picks the level");

        var direct = new Regex(@"Log(Error|Critical|Warning)\([^;]*FormatCompileFailureReport", RegexOptions.Singleline);
        // The reporter's own home is the one site allowed to format AND log the report (and its
        // XML docs quote the old LogError template that it replaced).
        var reporterHome = Path.Combine("MeshWeaver.Compiler", "CompileDiagnostics.cs");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetRelativePath(src, f) != reporterHome)
            .Where(f => direct.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f))
            .ToImmutableList();
        offenders.Should().BeEmpty(
            "logging FormatCompileFailureReport directly fixes the level by hand and skips the "
            + "retirement check (#5219). Offenders: " + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("MeshWeaver.slnx not found above the test bin");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> records = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
