using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🔴 <b>An adopt-time identity refusal is REPORTED by healability, once — Systemorph/MeshWeaver#5066.</b>
///
/// <para>The #3472 gate refuses a NodeType build whose record names another framework identity. It
/// was right every time; the report was wrong. Every guarded load path logged the refusal at
/// <c>Error</c> on every probe, under a sentence beginning "Nothing further is required". During
/// an unconverged roll the schema probe alone wrote 507 such lines in 60 minutes on one pod —
/// record <c>s7e280d1</c> vs process <c>se271838</c> on every one, while the record's assembly
/// version climbed v481 → v1338 — and that flood minted #5066.</para>
///
/// <para>Two properties, each pinned on a captured logger: the LEVEL branches on whether a local
/// compile heals the refusal (<c>Modules:RequirePrebuilt</c> ⇒ Error, otherwise Warning), and the
/// full-level line is written ONCE per (site, type, record identity). A guard over <c>src/</c> pins
/// that no load path logs the refusal around the ledger.</para>
/// </summary>
public class AdoptionRefusalIsClassifiedAndReportedOnceTest
{
    /// <summary>The record identity every #5066 sample line named.</summary>
    private const string RecordIdentity = "s7e280d1a0c5b7f5e2d1c3a4b5e6f7a8b";

    private static NodeTypeDefinition ForeignRecord(int version, string identity = RecordIdentity) => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = version,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = $"Acme_Widget/v{version}-{identity[..8]}-0123456789ab.dll",
        CompiledFrameworkVersion = identity,
    };

    private static NodeTypeAdoptionRefusalLog Ledger(bool requirePrebuilt)
        => Ledger(requirePrebuilt, out _);

    private static NodeTypeAdoptionRefusalLog Ledger(bool requirePrebuilt, out IConfigurationRoot configuration)
    {
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(requirePrebuilt
                ? ImmutableDictionary<string, string?>.Empty
                    .Add(PrebuiltAssemblySeeder.RequirePrebuiltConfigKey, "true")
                : ImmutableDictionary<string, string?>.Empty)
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<NodeTypeAdoptionRefusalLog>()
            .BuildServiceProvider();
        return services.GetRequiredService<NodeTypeAdoptionRefusalLog>();
    }

    [Fact]
    public void TheRecordIsActuallyRefused_SoEveryRowBelowExercisesTheRefusalPath()
    {
        NodeTypeBuildIdentity.Refuses(ForeignRecord(481)).Should().BeTrue(
            "the rows below are about how a refusal is REPORTED; a record this process would load "
            + "would make every one of them vacuous");
    }

    [Fact]
    public void OnAMeshThatCompilesLocally_TheExpectedRollTransitionIsAWarning_NotAnError()
    {
        var logger = new RecordingLogger();
        var ledger = Ledger(requirePrebuilt: false);

        ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "No hub configuration.");

        var line = logger.Records.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning,
            "where this mesh compiles locally the type's own hub rebuilds the build against the live "
            + "framework and restamps the record — the ordinary state after any platform roll. At "
            + "Error, one unconverged roll wrote 507 incident-grade lines an hour on one pod (#5066)");
        line.Message.Should().Contain(NodeTypeBuildIdentity.RecoveryVerbHealedHere)
            .And.NotContain("RequirePrebuilt",
                "the line states the ONE recovery that applies on this mesh, never both");
    }

    [Fact]
    public void OnARequirePrebuiltMesh_NothingHealsIt_SoItIsAnError()
    {
        var logger = new RecordingLogger();
        var ledger = Ledger(requirePrebuilt: true);

        ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "No hub configuration.");

        var line = logger.Records.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error,
            "on a Modules:RequirePrebuilt mesh no local compile is possible, so nothing on this "
            + "process heals the refusal — only a rebake and republish for this framework identity");
        line.Message.Should().Contain(NodeTypeBuildIdentity.RecoveryVerbRequirePrebuilt)
            .And.NotContain("Nothing further is required",
                "a line at Error must not tell its reader that nothing is required");
    }

    [Fact]
    public void TheSameRefusalProbedRepeatedly_IsReportedOnce_EvenAsTheAssemblyVersionClimbs()
    {
        var logger = new RecordingLogger();
        var ledger = Ledger(requirePrebuilt: false);

        // The #5066 series: the record's assembly version climbs while its framework identity
        // never moves — the same fact, over and over.
        foreach (var version in new[] { 481, 622, 635, 640, 874, 877, 1197, 1203, 1301, 1304, 1338 })
        foreach (var _ in Enumerable.Range(0, 40))
            ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(version), "No hub configuration.");

        logger.Records.Count(r => r.Level >= LogLevel.Information).Should().Be(1,
            "440 probes of one type against one record identity carry nothing the first line did "
            + "not — the 500th identical line is noise, and at probe rate it was the whole incident");
        logger.Records.Where(r => r.Level < LogLevel.Information)
            .Should().OnlyContain(r => r.Level == LogLevel.Debug, "repeats are demoted, not dropped silently");
    }

    [Fact]
    public void ANewRecordIdentity_OrAnotherType_OrAnotherSite_IsANewFact_AndIsReported()
    {
        var logger = new RecordingLogger();
        var ledger = Ledger(requirePrebuilt: false);

        ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "c").Should().BeTrue();
        ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(482), "c").Should().BeFalse();
        ledger.Report(logger, "SchemaProbe", "Acme/Widget",
            ForeignRecord(900, "saec4a2d0000000000000000000000000"), "c").Should().BeTrue(
            "the next roll stamps a new identity — a different fact, and it must be seen");
        ledger.Report(logger, "SchemaProbe", "Acme/Gadget", ForeignRecord(481), "c").Should().BeTrue(
            "another type is another fact");
        ledger.Report(logger, "CellSurface", "Acme/Widget", ForeignRecord(481), "c").Should().BeTrue(
            "each site states a different consequence of the same refusal");

        logger.Records.Count(r => r.Level == LogLevel.Warning).Should().Be(4);
    }

    [Fact]
    public void AReloadedRequirePrebuilt_ReclassifiesTheSameRefusal_AtItsNewLevel()
    {
        var logger = new RecordingLogger();
        var ledger = Ledger(requirePrebuilt: false, out var configuration);

        ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "c").Should().BeTrue();
        configuration[PrebuiltAssemblySeeder.RequirePrebuiltConfigKey] = "true";
        ledger.Report(logger, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "c").Should().BeTrue(
            "the same refusal now has nothing on this process to heal it — a different fact with a "
            + "different required response, so it must surface at Error rather than be suppressed");

        logger.Records.Select(r => r.Level).Should().Equal(LogLevel.Warning, LogLevel.Error);
    }

    [Fact]
    public void TwoMeshes_DoNotShareWhatWasAlreadyReported()
    {
        var first = new RecordingLogger();
        var second = new RecordingLogger();

        Ledger(requirePrebuilt: false).Report(first, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "c");
        Ledger(requirePrebuilt: false).Report(second, "SchemaProbe", "Acme/Widget", ForeignRecord(481), "c");

        second.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning,
            "the ledger is a mesh-scoped instance, never static state that bleeds across meshes");
    }

    /// <summary>
    /// 🚨 <b>No load path logs the refusal around the ledger.</b> Every site that refused a foreign
    /// build used to log <see cref="NodeTypeBuildIdentity.RecoveryVerb"/> at <c>Error</c> directly —
    /// five of them. A site that does so again re-opens #5066 at that site, invisibly.
    /// </summary>
    [Fact]
    public void NoSiteInSrcLogsTheIdentityRefusalExceptThroughTheLedger()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var direct = new Regex(@"NodeTypeBuildIdentity\.RecoveryVerb\b(?!For|HealedHere|RequirePrebuilt)");
        var reporter = new Regex(@"NodeTypeAdoptionRefusalLog>\(\)\s*\.Report\(");

        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: Path.GetRelativePath(src, f), Text: File.ReadAllText(f)))
            .ToImmutableList();

        var offenders = files
            .Where(f => direct.IsMatch(f.Text))
            .Select(f => f.Path)
            .ToImmutableList();
        offenders.Should().BeEmpty(
            "a refusal is logged through NodeTypeAdoptionRefusalLog.Report, which picks the level by "
            + "healability and reports once per (site, type, record identity); a direct "
            + "NodeTypeBuildIdentity.RecoveryVerb log line is the #5066 flood at that site. Offenders: "
            + string.Join(", ", offenders));

        var reportingSites = files.Sum(f => reporter.Matches(f.Text).Count);
        reportingSites.Should().BeGreaterThanOrEqualTo(5,
            "the five guarded load paths (schema probe, data-source schema, data-model area, cell "
            + "surface, contract handler) report through the ledger; fewer means the matcher went "
            + "blind and this guard would pass having checked nothing");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root (MeshWeaver.slnx).");
        return dir!.FullName;
    }

    private sealed class RecordingLogger : ILogger
    {
        private ImmutableList<(LogLevel Level, string Message)> records = ImmutableList<(LogLevel Level, string Message)>.Empty;

        public IReadOnlyList<(LogLevel Level, string Message)> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records = records.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
