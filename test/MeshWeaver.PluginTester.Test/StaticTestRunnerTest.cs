using System.Collections.Immutable;
using System.Text;
using MeshWeaver.PluginTester;
using MeshWeaver.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
// 🚨 xUnit v3 ships its OWN Xunit.TestContext — the very type the static lane's shim imitates.
// Aliased rather than disambiguated at each use, so this file can never assert against the wrong one.
using MeshTestContext = MeshWeaver.Testing.TestContext;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The STATIC lane's case surface, executed rather than described: a probe assembly is compiled to
/// disk and handed to <see cref="StaticTestRunner.Execute"/>, exactly as a NodeType's emitted
/// <c>Test/*.cs</c> is on a build run.
///
/// <para>🚨 <b>Why the counting assertions are the point.</b> <c>SkipException</c>,
/// <c>TestContext</c> and <c>TestLog</c> shipped in <c>Testing/TestContext.cs</c> and the runner
/// referenced NONE of them — a landed, dead surface. Wiring them in is only half the job: the half
/// that bites is a skip folded into the pass count, which makes a verdict line say <c>12/12
/// passed</c> over a suite where three cases declined to assert anything. That is the
/// absence-of-evidence-reads-as-green failure this estate has paid for repeatedly, so every count
/// here is asserted as a SPLIT, never as a total.</para>
/// </summary>
public class StaticTestRunnerTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One case per outcome the runner can produce, in one assembly — a pass, a decline, a
    /// failure, and a hosted case the mesh lane owns.
    /// </summary>
    private const string OutcomesProbe = """
        using System;
        using MeshWeaver.Testing;

        public static class ProbeOutcomesTests
        {
            public static void APassingCase() { }

            public static void ADecliningCase() =>
                throw new SkipException("no credential on this host");

            public static void AFailingCase() =>
                throw new InvalidOperationException("boom");

            public static void AHostedCase(object host) { }
        }
        """;

    [Fact]
    public void ASkipIsItsOwnOutcome_CarriesItsReason_AndIsNeverCountedAsAPass()
    {
        var directory = TempDirectory("skip-outcome");
        try
        {
            var log = new StringWriter();
            var run = StaticTestRunner.Execute(
                Emit(directory, "Probe.Outcomes", OutcomesProbe), [], Budget, log);

            Assert.Null(run.LoadError);
            Assert.Equal(4, run.Cases.Length);

            var skipped = Assert.Single(run.Cases.Where(c => c.Name == "ProbeOutcomesTests.ADecliningCase"));
            Assert.Equal(StaticTestRunner.Outcome.Skipped, skipped.Outcome);

            // The reason REACHES the verdict. A skip whose reason is dropped is indistinguishable
            // from a case that was never discovered.
            Assert.Equal("no credential on this host", skipped.Error);

            // 🚨 The split, member by member. `Passed` counting 2 here would be the whole defect.
            Assert.Equal(1, run.Passed);
            Assert.Equal(1, run.Failed);
            Assert.Equal(1, run.Skipped);
            Assert.Equal(1, run.NeedsMesh);
            Assert.Equal(run.Cases.Length, run.Passed + run.Failed + run.Skipped + run.NeedsMesh);

            // A skip does not turn the run red on its own — but this run has a real failure, so it
            // is red for that reason and only that reason.
            Assert.False(run.IsGreen);

            // …and the printed line gets its own token, because the table a reader actually looks
            // at is where a skip would otherwise disappear into `ok`.
            var printed = log.ToString();
            Assert.Contains("SKIP  ProbeOutcomesTests.ADecliningCase", printed, StringComparison.Ordinal);
            Assert.Contains("no credential on this host", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("ok    ProbeOutcomesTests.ADecliningCase", printed, StringComparison.Ordinal);
        }
        finally { Cleanup(directory); }
    }

    /// <summary>A suite whose only non-passing case declines: green, and honest about it.</summary>
    private const string AllSkippedProbe = """
        using MeshWeaver.Testing;

        public static class ProbeDeclineTests
        {
            public static void One() => throw new SkipException("not on this platform");
            public static void Two() => throw new SkipException("not on this platform");
        }
        """;

    [Fact]
    public void ARunOfNothingButSkipsIsGreen_AndReportsZeroPassed()
    {
        var directory = TempDirectory("all-skipped");
        try
        {
            var run = StaticTestRunner.Execute(
                Emit(directory, "Probe.Decline", AllSkippedProbe), [], Budget, null);

            // Green — declining is a legitimate answer and must not turn a build red…
            Assert.True(run.IsGreen);
            // …but it proved NOTHING, and the numbers say so.
            Assert.Equal(0, run.Passed);
            Assert.Equal(2, run.Skipped);
            Assert.Equal(0, run.Failed);
        }
        finally { Cleanup(directory); }
    }

    private const string LogProbe = """
        using System;
        using MeshWeaver.Testing;

        public static class ProbeLogTests
        {
            public static void AFailureNarratesItself()
            {
                TestLog.WriteLine("opened the door");
                TestLog.WriteLine("read {0} rows", 7);
                throw new InvalidOperationException("then it broke");
            }

            public static void APassIsQuietInTheLogButKeepsItsLines()
            {
                TestLog.WriteLine("nothing to see");
            }
        }
        """;

    [Fact]
    public void TestLogIsCapturedPerCase_AttachedToItsResult_AndPrintedForFailuresOnly()
    {
        var directory = TempDirectory("testlog");
        try
        {
            var log = new StringWriter();
            var run = StaticTestRunner.Execute(
                Emit(directory, "Probe.Log", LogProbe), [], Budget, log);

            Assert.Null(run.LoadError);
            var failed = Assert.Single(run.Cases.Where(c => c.Name == "ProbeLogTests.AFailureNarratesItself"));
            var passed = Assert.Single(run.Cases.Where(c => c.Name == "ProbeLogTests.APassIsQuietInTheLogButKeepsItsLines"));

            // Attached to the CASE, not smeared across the build log: each case's lines are its own,
            // prefixed with its own name.
            Assert.Equal(2, failed.Log.Length);
            Assert.Contains("[ProbeLogTests.AFailureNarratesItself] opened the door", failed.Log[0], StringComparison.Ordinal);
            Assert.Contains("[ProbeLogTests.AFailureNarratesItself] read 7 rows", failed.Log[1], StringComparison.Ordinal);
            Assert.Equal(
                ["      [ProbeLogTests.APassIsQuietInTheLogButKeepsItsLines] nothing to see"],
                passed.Log);

            // Printed with the failure — that is the point of capturing it — and NOT with the pass,
            // which would bury the failure's neighbourhood in noise.
            var printed = log.ToString();
            Assert.Contains("opened the door", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("nothing to see", printed, StringComparison.Ordinal);
        }
        finally { Cleanup(directory); }
    }

    private const string ContextProbe = """
        using System;
        using System.Threading;
        using MeshWeaver.Testing;

        public static class ProbeContextTests
        {
            public static void SeesItsOwnName()
            {
                var name = TestContext.Current.CaseName;
                if (name != "ProbeContextTests.SeesItsOwnName")
                    throw new InvalidOperationException("CaseName was '" + name + "'");
            }

            public static void SeesACancellableBudget()
            {
                var token = TestContext.Current.CancellationToken;
                if (!token.CanBeCanceled)
                    throw new InvalidOperationException("the case budget was CancellationToken.None");
                if (token.IsCancellationRequested)
                    throw new InvalidOperationException("the budget was already spent on entry");
            }
        }
        """;

    [Fact]
    public void TestContextCurrentIsPopulatedForTheCase()
    {
        var directory = TempDirectory("context");
        try
        {
            var run = StaticTestRunner.Execute(
                Emit(directory, "Probe.Context", ContextProbe), [], Budget, null);

            Assert.Null(run.LoadError);
            Assert.All(run.Cases, c => Assert.Equal(StaticTestRunner.Outcome.Passed, c.Outcome));
            Assert.Equal(2, run.Passed);
        }
        finally { Cleanup(directory); }
    }

    private const string CooperativeHangProbe = """
        using System;
        using MeshWeaver.Testing;

        public static class ProbeBudgetTests
        {
            public static void WaitsOnItsOwnBudget()
            {
                TestLog.WriteLine("about to wait on the budget");
                TestContext.Current.CancellationToken.WaitHandle.WaitOne();
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            }
        }
        """;

    [Fact]
    public void TheBudgetTokenTripsInsideTheCap_SoACooperativeCaseEndsNamedRatherThanAbandoned()
    {
        var directory = TempDirectory("budget");
        try
        {
            var log = new StringWriter();
            var run = StaticTestRunner.Execute(
                Emit(directory, "Probe.Budget", CooperativeHangProbe), [],
                TimeSpan.FromSeconds(2), log);

            var c = Assert.Single(run.Cases);
            Assert.Equal(StaticTestRunner.Outcome.Failed, c.Outcome);

            // The token fired, so the case ended by NAME — not with the runner's "did not return"
            // verdict, which means the thread was abandoned and the build carried a live thread on.
            Assert.Contains("OperationCanceledException", c.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("did not return within", c.Error!, StringComparison.Ordinal);

            // …and the verdict NAMES the budget rather than repeating the framework's
            // "The operation was canceled.", which tells a reader nothing about why.
            Assert.Contains("the case budget (2s) expired", c.Error!, StringComparison.Ordinal);

            // …and the lines it wrote before it blocked survived into the report.
            Assert.Contains("about to wait on the budget", string.Join("\n", c.Log), StringComparison.Ordinal);
            Assert.Contains("about to wait on the budget", log.ToString(), StringComparison.Ordinal);
        }
        finally { Cleanup(directory); }
    }

    private const string ParallelProbeTemplate = """
        using MeshWeaver.Testing;

        public static class Probe{0}Tests
        {{
            public static void Case() => TestLog.WriteLine("marker-{0}");
        }}
        """;

    [Fact]
    public async Task ConcurrentRunsDoNotSmearEachOthersLog()
    {
        // The cascade builds packages in PARALLEL, so two Execute calls are routinely in flight at
        // once. A process-wide TestLog sink would attribute one package's output to another; the
        // capture hangs off the [ThreadStatic] context instead, and this is what proves it.
        var directory = TempDirectory("parallel");
        try
        {
            var assemblies = Enumerable.Range(0, 8)
                .Select(i => Emit(
                    directory,
                    $"Probe.Parallel{i}",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, ParallelProbeTemplate, i)))
                .ToArray();

            var runs = await Task.WhenAll(assemblies.Select((path, i) =>
                Task.Run(() => (Index: i, Run: StaticTestRunner.Execute(path, [], Budget, null)))));

            Assert.All(runs, r =>
            {
                var c = Assert.Single(r.Run.Cases);
                Assert.Equal(StaticTestRunner.Outcome.Passed, c.Outcome);
                var line = Assert.Single(c.Log);
                Assert.Contains($"marker-{r.Index}", line, StringComparison.Ordinal);
                Assert.Contains($"Probe{r.Index}Tests.Case", line, StringComparison.Ordinal);
            });
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void EnteringACaseContextInstallsItAndDisposingRestoresTheOneBefore()
    {
        Assert.Equal("(no case)", MeshTestContext.Current.CaseName);
        using (MeshTestContext.Enter("Outer.Case", CancellationToken.None))
        {
            Assert.Equal("Outer.Case", MeshTestContext.Current.CaseName);
            using (MeshTestContext.Enter("Inner.Case", CancellationToken.None))
                Assert.Equal("Inner.Case", MeshTestContext.Current.CaseName);
            Assert.Equal("Outer.Case", MeshTestContext.Current.CaseName);
        }

        // Cleared after — a leaked context would name the wrong case in every later TestLog line
        // this thread emits, and the thread is reused for the next case.
        Assert.Equal("(no case)", MeshTestContext.Current.CaseName);
    }

    [Fact]
    public void DisposingAnotherThreadsScopeCannotClearThisThreadsContext()
    {
        // The context is [ThreadStatic]. A scope disposed from a foreign thread must NOT reach into
        // that thread's slot — doing so would silently unset a different, concurrently running case.
        IDisposable? foreign = null;
        var owner = new Thread(() => foreign = MeshTestContext.Enter("Foreign.Case", CancellationToken.None));
        owner.Start();
        owner.Join();

        using (MeshTestContext.Enter("Mine.Case", CancellationToken.None))
        {
            foreign!.Dispose();
            Assert.Equal("Mine.Case", MeshTestContext.Current.CaseName);
        }
        Assert.Equal("(no case)", MeshTestContext.Current.CaseName);
    }

    [Fact]
    public void TestLogOutsideACaseFallsBackToTheProcessSink()
    {
        var seen = new List<string>();
        var previous = TestLog.Sink;
        try
        {
            TestLog.Sink = seen.Add;
            TestLog.WriteLine("orphan line");
            Assert.Equal(["      [(no case)] orphan line"], seen);
        }
        finally { TestLog.Sink = previous; }
    }

    [Fact]
    public void APackageRollUpKeepsSkipsOutOfItsPassCount()
    {
        // The counting seam one level up: CascadeBuild's package roll-up is where a per-case skip
        // would be laundered into "N passed" for the table and the JSON report.
        var run = new StaticTestRunner.Run("x.dll",
        [
            new StaticTestRunner.Case("T.A", StaticTestRunner.Outcome.Passed, TimeSpan.Zero, null),
            new StaticTestRunner.Case("T.B", StaticTestRunner.Outcome.Skipped, TimeSpan.Zero, "why"),
            new StaticTestRunner.Case("T.C", StaticTestRunner.Outcome.Skipped, TimeSpan.Zero, "why"),
            new StaticTestRunner.Case("T.D", StaticTestRunner.Outcome.NeedsMesh, TimeSpan.Zero, "host"),
        ], null);

        var package = new CascadeBuild.PackageBuild(
            "Probe",
            [new CascadeBuild.TypeBuild("Probe/T", "Probe", null, TimeSpan.Zero, 1, "x.dll", run, [])],
            [], TimeSpan.Zero, TimeSpan.Zero, []);

        Assert.Equal(1, package.TestsPassed);
        Assert.Equal(2, package.TestsSkipped);
        Assert.Equal(0, package.TestsFailed);
        Assert.Equal(1, package.TestsNeedMesh);
        Assert.True(package.IsGreen);
    }

    [Fact]
    public void TheSummaryTableCarriesASkipColumn_AlignedWithItsHeader()
    {
        // 🚨 The one table a reader actually looks at. Before this facility it had no `skip` column
        // at all, so a declining case simply was not in the picture; and the header had already
        // drifted out of alignment with the row format, which is how a column quietly stops meaning
        // what its heading says.
        var run = new StaticTestRunner.Run("x.dll",
        [
            new StaticTestRunner.Case("T.A", StaticTestRunner.Outcome.Passed, TimeSpan.Zero, null),
            new StaticTestRunner.Case("T.B", StaticTestRunner.Outcome.Skipped, TimeSpan.Zero, "why"),
            new StaticTestRunner.Case("T.C", StaticTestRunner.Outcome.Skipped, TimeSpan.Zero, "why"),
            new StaticTestRunner.Case("T.D", StaticTestRunner.Outcome.Skipped, TimeSpan.Zero, "why"),
        ], null);
        var package = new CascadeBuild.PackageBuild(
            "Probe",
            [new CascadeBuild.TypeBuild("Probe/T", "Probe", null, TimeSpan.Zero, 1, "x.dll", run, [])],
            [], TimeSpan.Zero, TimeSpan.Zero, []);
        var report = new CascadeBuild.Report(
            "identity",
            [new Cascade.NodeResult<CascadeBuild.PackageBuild>(
                "Probe", Cascade.NodeOutcome.Green, package, null, null,
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)],
            ["Probe"], TimeSpan.FromSeconds(1), []);

        var output = new StringWriter();
        CascadeBuild.Print(output, report, _ => []);
        var lines = output.ToString().Split('\n');

        var header = Assert.Single(lines, l => l.StartsWith("package", StringComparison.Ordinal));
        var row = Assert.Single(lines, l => l.StartsWith("Probe ", StringComparison.Ordinal));

        var skip = header.IndexOf("skip", StringComparison.Ordinal);
        var passed = header.IndexOf("passed", StringComparison.Ordinal);
        Assert.True(skip > 0, $"the summary table has no `skip` column: {header}");
        Assert.True(passed > 0 && passed < skip, "passed and skip are separate columns, in that order");

        // The numbers land UNDER their own headings — one pass, three declines, and the pass count
        // is 1 rather than 4.
        // `passed` is right-aligned in a 6-wide field it exactly fills; `skip` is right-aligned in
        // a 5-wide one, so its field starts one column left of the heading.
        Assert.Equal("1", row[passed..(passed + 6)].Trim());
        Assert.Equal("3", row[(skip - 1)..(skip + 4)].Trim());
        Assert.Equal(1, package.TestsPassed);
        Assert.Equal(3, package.TestsSkipped);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> to a real assembly on disk, referencing this process's
    /// core assemblies plus <c>mw-plugin-test</c> itself — which is how a case reaches
    /// <c>MeshWeaver.Testing</c>. The runner loads it into a collectible context that defers every
    /// already-loaded assembly to the default one, so <c>SkipException</c> thrown in there IS the
    /// <c>SkipException</c> the runner catches; a second copy would silently classify every skip as
    /// a failure.
    /// </summary>
    private static string Emit(string directory, string name, string source)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(path);
        Assert.True(
            emit.Success,
            "the probe assembly did not compile: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    private static ImmutableArray<MetadataReference> References() =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(SkipException).Assembly.Location),
        .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Where(p => Path.GetFileName(p)
                is "System.Runtime.dll" or "netstandard.dll" or "System.Threading.dll"
                or "System.Console.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))),
    ];

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), "static-test-runner-" + prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* best effort — a locked temp dir must not fail the test */ }
    }
}
