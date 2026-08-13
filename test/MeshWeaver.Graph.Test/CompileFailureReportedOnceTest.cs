using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A compile failure is reported EXACTLY ONCE — the log-once contract of the compile pipeline.
///
/// <para><b>The production defect.</b> <c>MeshNodeCompilationService</c> logged ~150 ERROR
/// lines/24h across the memex / memex-cloud / atioz portals, and every stack pointed at
/// <c>EmitToDiskWithRetry → CompileToDiskAsync</c>, which reads like an emit/IO fault. It is not:
/// the disk-emit self-heal has fired ZERO times in 7 days of production. Those ~150 lines were
/// ~72 genuine Roslyn compile errors in mesh content, each logged TWICE — once by the emit site
/// (<c>logger.LogError(errorMessage); throw new CompilationException(...)</c>, the classic
/// log-and-throw) and again by the pipeline's <c>.Catch&lt;…, CompilationException&gt;</c> funnel
/// in <c>CompileAsyncCore</c>. The duplicate came FIRST and carried no exception, no stack and no
/// source-discovery report, so it fingerprinted as a second, distinct fault whose only visible
/// frame was the emit path — which is exactly why "your C# does not compile" was read for weeks
/// as "the emit is failing".</para>
///
/// <para><b>The contract these tests pin.</b> The emit leaf THROWS but never LOGS; the
/// exception it throws carries the complete diagnostics so the single funnel can report the whole
/// failure. Anything the emit path does log (the lost-write self-heal) stays, because that
/// condition never reaches the funnel as an exception on its own.</para>
/// </summary>
public class CompileFailureReportedOnceTest : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), $"mesh-emit-logonce-{Guid.NewGuid():N}");

    public CompileFailureReportedOnceTest() => Directory.CreateDirectory(_cacheDir);

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Captures every record written through it, so a test can assert on log VOLUME.</summary>
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

    // Instance (never static — AGENTS.md "no static collections"): the runtime's reference set,
    // so Roslyn compiles these snippets exactly as it does a real node's source.
    private readonly IReadOnlyList<MetadataReference> references =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

    /// <summary>
    /// A real, genuinely-broken compilation — the same failure shape production hits
    /// (<c>CS0103: The name 'host' does not exist in the current context</c>, the prod
    /// <c>Doc/DataMesh/SocialMedia/Post</c> failure). Roslyn is run for real: no fake emit,
    /// so the diagnostics and the throw are the ones the service actually produces.
    /// </summary>
    private CSharpCompilation BrokenCompilation(string assemblyName) =>
        CSharpCompilation.Create(
            assemblyName,
            syntaxTrees:
            [
                CSharpSyntaxTree.ParseText(
                    """
                    public static class Broken
                    {
                        public static string Render() => host.Localize("ui.mdNoPosts");
                    }
                    """)
            ],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private CSharpCompilation ValidCompilation(string assemblyName) =>
        CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [CSharpSyntaxTree.ParseText("public static class Fine { public static int Answer => 42; }")],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void Failed_emit_throws_the_full_diagnostics_and_logs_nothing()
    {
        // This is the whole prod defect in one assertion: the emit leaf must hand the failure UP,
        // not report it. Logging here as well is what doubled every compile failure in the portals'
        // error logs — and the duplicate carried no exception, so it read as an emit/IO fault.
        const string nodeName = "Demo_BrokenSource";
        var logger = new RecordingLogger();

        var ex = Assert.Throws<CompilationException>(() =>
            MeshNodeCompilationService.EmitToDiskWithRetry(
                _cacheDir, nodeName, MeshNodeCompilationService.DiskEmitAttempts, logger,
                releaseDir => MeshNodeCompilationService.EmitCompilationToDirectory(
                    BrokenCompilation(nodeName), nodeName, "Acme/BrokenSource", releaseDir,
                    CancellationToken.None)));

        // The exception carries EVERYTHING the single funnel needs to report the failure once.
        ex.NodePath.Should().Be("Acme/BrokenSource");
        ex.Message.Should().Contain("CS0103", "the diagnostics travel on the exception, not in a log line");
        ex.Message.Should().Contain("host", "the exception names the symbol that failed to resolve");

        logger.Records.Should().BeEmpty(
            "a compile failure is reported EXACTLY ONCE, by the pipeline's "
            + ".Catch<…, CompilationException> funnel — the only site that also has the exception, "
            + "its stack and the source-discovery report. Logging it at the emit site too is what "
            + "made ~72 real failures show up as ~150 ERROR lines/24h in production");
    }

    [Fact]
    public void A_failing_emit_is_attempted_once_and_leaves_no_artifact()
    {
        // Bounds the fix: removing the log must not turn a deterministic compile error into a
        // retried one (three Roslyn runs per broken node), and the failed emit must still leave
        // the discovery namespace clean.
        const string nodeName = "Demo_BrokenOnce";
        var emits = 0;

        Assert.Throws<CompilationException>(() =>
            MeshNodeCompilationService.EmitToDiskWithRetry(
                _cacheDir, nodeName, MeshNodeCompilationService.DiskEmitAttempts, new RecordingLogger(),
                releaseDir =>
                {
                    emits++;
                    return MeshNodeCompilationService.EmitCompilationToDirectory(
                        BrokenCompilation(nodeName), nodeName, "Acme/BrokenOnce", releaseDir,
                        CancellationToken.None);
                }));

        emits.Should().Be(1, "a deterministic compile error must NOT be retried");
        Directory.GetDirectories(_cacheDir, $"{nodeName}_*").Should()
            .BeEmpty("a failed compile leaves no discoverable artifact");
        Directory.GetDirectories(_cacheDir, ".staging-*").Should()
            .BeEmpty("the failed staging dir is cleaned up");
    }

    [Fact]
    public void A_successful_emit_writes_the_assembly_and_logs_nothing()
    {
        // Control: the extracted emit really does emit — dll + pdb + XML doc land in the release
        // dir and the happy path is silent too (its Debug-level record belongs to the caller).
        const string nodeName = "Demo_ValidSource";
        var logger = new RecordingLogger();

        var dllPath = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, MeshNodeCompilationService.DiskEmitAttempts, logger,
            releaseDir => MeshNodeCompilationService.EmitCompilationToDirectory(
                ValidCompilation(nodeName), nodeName, "Acme/ValidSource", releaseDir,
                CancellationToken.None));

        new FileInfo(dllPath).Length.Should().BeGreaterThan(0);
        var releaseDir = Path.GetDirectoryName(dllPath)!;
        File.Exists(Path.Combine(releaseDir, $"{nodeName}.pdb")).Should().BeTrue();
        File.Exists(Path.Combine(releaseDir, $"DynamicNode_{nodeName}.xml")).Should().BeTrue();
        logger.Records.Should().BeEmpty("a clean emit has nothing to say");
    }

    [Fact]
    public void The_lost_write_self_heal_still_warns_because_it_never_reaches_the_funnel()
    {
        // The one thing the emit path legitimately logs. A lost write is recovered in-place and
        // NEVER surfaces as an exception, so the .Catch funnel cannot report it — without this
        // warning the self-heal would be invisible. Silencing it would be the over-correction.
        const string nodeName = "Demo_LostWrite";
        var logger = new RecordingLogger();
        var emits = 0;

        MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, MeshNodeCompilationService.DiskEmitAttempts, logger,
            releaseDir =>
            {
                emits++;
                var emitted = MeshNodeCompilationService.EmitCompilationToDirectory(
                    ValidCompilation(nodeName), nodeName, "Acme/LostWrite", releaseDir,
                    CancellationToken.None);
                if (emits == 1)
                    File.Delete(emitted.DllPath); // the ephemeral-/tmp eviction
                return emitted;
            });

        emits.Should().Be(2);
        logger.Records.Select(r => r.Level).Should()
            .Equal([LogLevel.Warning], "the lost-write self-heal is the emit path's ONLY log — "
                                       + "it is invisible to the funnel, so it must report itself");
    }

    /// <summary>
    /// The emit canary (issue #890) must ANSWER, not throw. It runs only when Roslyn's
    /// <c>Emit</c> has already thrown, so a diagnostic that faults there would replace the
    /// original exception and destroy the evidence it exists to preserve.
    /// </summary>
    [Fact]
    public void The_emit_canary_reports_healthy_shared_state_on_a_healthy_process()
    {
        var verdict = MeshNodeCompilationService.ProbeSharedEmitState(ValidCompilation("Demo_Canary"));

        verdict.Should().StartWith("canary=OK",
            "nothing has poisoned this process, so a trivial nested-generic emit against the "
            + "same reference set must still succeed — that is the branch that tells triage the "
            + "fault is specific to the failing compilation's own inputs");
    }

    /// <summary>
    /// …and it must still answer when the reference set it is handed is unusable — the case
    /// where the canary's verdict is the WHOLE point. An empty reference set cannot bind
    /// <c>object</c>, so this stands in for a poisoned shared reference set without needing to
    /// corrupt the real one.
    /// </summary>
    [Fact]
    public void The_emit_canary_answers_instead_of_throwing_when_the_reference_set_is_unusable()
    {
        var unusable = CSharpCompilation.Create(
            "Demo_CanaryNoRefs",
            syntaxTrees: [CSharpSyntaxTree.ParseText("public class X { }")],
            references: [],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var verdict = MeshNodeCompilationService.ProbeSharedEmitState(unusable);

        verdict.Should().NotBeNullOrWhiteSpace();
        verdict.Should().NotStartWith("canary=OK",
            "a reference set that cannot bind a trivial compilation is exactly the "
            + "process-wide-breakage branch, and the canary must say so rather than throw");
    }
}
