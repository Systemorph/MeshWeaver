using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The <c>compile</c> bake compiles its NodeTypes in PARALLEL (<see cref="BakeSchedule"/>). Two
/// claims carry that change, and each has a case with a negative control:
/// <list type="number">
///   <item>a type ordered after another NEVER starts before that one has finished — and the same
///     check, run with the edges withheld, DOES see a violation, so the positive arm is the edge
///     doing its job rather than the timing happening to be kind;</item>
///   <item>a parallel bake writes the bytes a serial one writes — every bundle entry, the edge
///     file, the identity file — and the same per-type verdicts and log lines. The bar is the
///     serial bake's OWN reproducibility: the emit is not deterministic (a fresh MVID per compile),
///     so two serial bakes are compared first and the comparison masks exactly the fields they
///     differ in (<see cref="Normalize"/>). The same comparison over a bake whose source changed in
///     one type DOES report that type's assembly and no other, so a comparison that could not see
///     a difference is ruled out.</item>
/// </list>
/// </summary>
public partial class ParallelBakeTest(ITestOutputHelper output)
{
    /// <summary>The test output, for the parts of this class declared in other files.</summary>
    private ITestOutputHelper Output => output;

    // ── 1. dependency order ───────────────────────────────────────────────────────────────────

    /// <summary>Base ← Mid ← Top is a chain; Free waits on nothing.</summary>
    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Chain =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["Base"] = [],
            ["Mid"] = ["Base"],
            ["Top"] = ["Mid"],
            ["Free"] = [],
        }.ToImmutableDictionary(StringComparer.Ordinal);

    [Fact(Timeout = 60_000)]
    public async Task ADependentNeverStartsBeforeItsDependencyFinished_AndWithoutTheEdgeItDoes()
    {
        var ct = TestContext.Current.CancellationToken;

        var honoured = await RecordSchedule(Chain, ct);
        output.WriteLine("with edges:    " + string.Join(" ", honoured));
        Assert.Empty(Violations(honoured, Chain));
        // Parallelism is real, not a serial run that trivially honours every edge: Free ran while
        // Base was still parked waiting for a Mid that could not start.
        Assert.True(
            honoured.IndexOf("start:Free") < honoured.IndexOf("end:Base"),
            "Free has no dependency and must run beside Base — " + string.Join(" ", honoured));

        // NEGATIVE CONTROL — the same work with the edges withheld. Base parks until Mid starts,
        // which now happens at once, so the violation the check exists to catch must appear.
        var withheld = await RecordSchedule(
            Chain.ToImmutableDictionary(kv => kv.Key, _ => ImmutableArray<string>.Empty, StringComparer.Ordinal),
            ct,
            checkAgainst: Chain,
            midWait: SignalBound);
        output.WriteLine("without edges: " + string.Join(" ", withheld));
        Assert.Contains("Mid started before Base finished", Violations(withheld, Chain));
    }

    /// <summary>
    /// Bound on waiting for a worker that MUST arrive (dispatch on a busy pool can be slow) — long,
    /// because it only ever ends early on the signal; it is a cap on a hang, never a timing claim.
    /// </summary>
    private static readonly TimeSpan SignalBound = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs the four ids through <see cref="BakeSchedule.Run{T}"/> with four slots. Base PARKS until
    /// Free has FINISHED — so "Free ran beside Base" never depends on how fast the pool dispatches —
    /// and then until Mid has started, for <paramref name="midWait"/>. With the edge honoured Mid
    /// cannot start, so that short wait lapses and Base ends first; with the edge withheld the caller
    /// passes the long <see cref="SignalBound"/>, Mid starts whenever it is dispatched, and Base ends
    /// after it. Each park is a volatile flag polled under a bounded SpinWait, written in a
    /// <c>finally</c>.
    /// </summary>
    private static Task<ImmutableList<string>> RecordSchedule(
        ImmutableDictionary<string, ImmutableArray<string>> buildAfter,
        CancellationToken ct,
        ImmutableDictionary<string, ImmutableArray<string>>? checkAgainst = null,
        TimeSpan? midWait = null)
    {
        var events = ImmutableList<string>.Empty;
        var midStarted = 0;
        var freeEnded = 0;
        void Record(string e) => ImmutableInterlocked.Update(ref events, l => l.Add(e));

        return BakeSchedule.Run(
                (checkAgainst ?? buildAfter).Keys.ToArray(),
                buildAfter,
                id =>
                {
                    Record("start:" + id);
                    try
                    {
                        if (id == "Mid")
                            Volatile.Write(ref midStarted, 1);
                        if (id == "Base")
                        {
                            SpinWait.SpinUntil(() => Volatile.Read(ref freeEnded) == 1, SignalBound);
                            SpinWait.SpinUntil(() => Volatile.Read(ref midStarted) == 1, midWait ?? TimeSpan.FromMilliseconds(250));
                        }
                        return id;
                    }
                    finally
                    {
                        Record("end:" + id);
                        if (id == "Free")
                            Volatile.Write(ref freeEnded, 1);
                    }
                },
                maxParallel: 4)
            .Select(_ => Volatile.Read(ref events))
            .Await(ct);
    }

    private static ImmutableArray<string> Violations(
        ImmutableList<string> events, ImmutableDictionary<string, ImmutableArray<string>> buildAfter) =>
    [
        .. from kv in buildAfter
           from dep in kv.Value
           where events.IndexOf("start:" + kv.Key) < events.IndexOf("end:" + dep)
           select $"{kv.Key} started before {dep} finished",
    ];

    [Fact]
    public void BuildAfter_IsTheRuntimesOrder_WithCycleEdgesRemoved()
    {
        var after = BakeSchedule.BuildAfter(
        [
            ("Ctrl/Uses", new NodeTypeDefinition { Sources = ["shared=@Ctrl/Base/Source"] }),
            ("Ctrl/Base", new NodeTypeDefinition()),
            ("Ctrl/X", new NodeTypeDefinition { Sources = ["shared=@Ctrl/Y/Source"] }),
            ("Ctrl/Y", new NodeTypeDefinition { Sources = ["shared=@Ctrl/X/Source"] }),
        ]);

        Assert.Equal(["Ctrl/Base"], after["Ctrl/Uses"]);
        Assert.Empty(after["Ctrl/Base"]);
        Assert.Empty(after["Ctrl/X"]); // a cycle waits on nothing inside itself, or nothing in it would ever start
        Assert.Empty(after["Ctrl/Y"]);
    }

    /// <summary>
    /// A path the tree carries twice (<c>Ctrl/Uses.json</c> and <c>Ctrl/Uses/index.json</c>) is
    /// compiled twice inside its one work item, so EVERY definition's dependencies order that item —
    /// not only the first's. Negative control: from the first definition alone, it waits on nothing.
    /// </summary>
    [Fact]
    public void ScheduleOf_APathCarriedTwice_WaitsForTheDependenciesOfEveryDefinition()
    {
        var byPath = new Dictionary<string, ImmutableArray<(string Path, NodeTypeDefinition? Definition)>>(StringComparer.Ordinal)
        {
            ["Ctrl/Base"] = [("Ctrl/Base", new NodeTypeDefinition())],
            ["Ctrl/Uses"] =
            [
                ("Ctrl/Uses", new NodeTypeDefinition()),
                ("Ctrl/Uses", new NodeTypeDefinition { Sources = ["shared=@Ctrl/Base/Source"] }),
            ],
        };

        Assert.Equal(["Ctrl/Base"], TreeBake.ScheduleOf(byPath.Values)["Ctrl/Uses"]);
        Assert.Empty(BakeSchedule.BuildAfter(byPath.Values.Select(g => g[0]))["Ctrl/Uses"]);
    }

    /// <summary>
    /// The bake's work items are ORDINAL paths and the dependency graph is case-insensitive. A
    /// case-only pair (<c>Ctrl/Foo</c>, <c>Ctrl/foo</c>) used to throw out of a case-insensitive
    /// map and abort the whole bake; now both keep their own entry, and a type that pulls the
    /// pair's source waits for BOTH spellings. Negative control: the unrelated type waits on nothing.
    /// </summary>
    [Fact]
    public void BuildAfter_KeepsACaseOnlyPairApart_AndOrdersADependentAfterBoth()
    {
        var after = BakeSchedule.BuildAfter(
        [
            ("Ctrl/Foo", new NodeTypeDefinition()),
            ("Ctrl/foo", new NodeTypeDefinition()),
            ("Ctrl/Uses", new NodeTypeDefinition { Sources = ["shared=@Ctrl/foo/Source"] }),
            ("Ctrl/Free", new NodeTypeDefinition()),
        ]);

        Assert.Equal(["Ctrl/Foo", "Ctrl/Free", "Ctrl/Uses", "Ctrl/foo"], after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(after["Ctrl/Foo"]);
        Assert.Empty(after["Ctrl/foo"]);
        Assert.Equal(["Ctrl/Foo", "Ctrl/foo"], after["Ctrl/Uses"]);
        Assert.Empty(after["Ctrl/Free"]);
    }

    /// <summary>
    /// The bake's shared resources outlive every compile that entered: terminating the pipeline
    /// while a compile runs cancels it and refuses new ones, but cleans up only when that compile
    /// has left — and exactly once. Negative control: with nothing inside, termination cleans at once.
    /// </summary>
    [Fact]
    public void BakeLifetime_CleansUpOnlyAfterTheLastCompileLeft_AndRefusesNewOnes()
    {
        var cleaned = 0;
        var lifetime = new TreeBake.BakeLifetime(new CancellationTokenSource(), () => cleaned++);
        Assert.True(lifetime.TryEnter());

        lifetime.Terminate();
        Assert.True(lifetime.Token.IsCancellationRequested, "termination cancels the running compiles");
        Assert.Equal(0, cleaned); // a compile is still inside — its resolver and directory must survive
        Assert.False(lifetime.TryEnter(), "no compile starts after termination");
        Assert.Equal(0, cleaned);

        lifetime.Exit();
        Assert.Equal(1, cleaned);
        lifetime.Terminate();
        Assert.Equal(1, cleaned);

        var idleCleaned = 0;
        new TreeBake.BakeLifetime(new CancellationTokenSource(), () => idleCleaned++).Terminate();
        Assert.Equal(1, idleCleaned);
    }

    /// <summary>
    /// The last compile leaving while termination is cancelling must not run the cleanup — which
    /// disposes the cancellation source — before <c>Cancel()</c> has returned. A cancellation
    /// callback stands in for that compile (callbacks run inside <c>Cancel()</c>, newest first):
    /// it leaves, and a callback registered earlier, so running later, marks the point at which
    /// cancellation is still in progress. The cleanup must observe it finished.
    /// </summary>
    [Fact]
    public void BakeLifetime_LastCompileLeavingDuringCancel_CleansOnlyAfterCancelReturned()
    {
        using var source = new CancellationTokenSource();
        var callbacksDone = false;
        bool? cleanedAfterCancel = null;
        var lifetime = new TreeBake.BakeLifetime(source, () => cleanedAfterCancel = callbacksDone);
        Assert.True(lifetime.TryEnter());
        source.Token.Register(() => callbacksDone = true); // runs LAST
        source.Token.Register(lifetime.Exit);              // runs FIRST: the last compile leaves

        lifetime.Terminate();

        Assert.True(cleanedAfterCancel, "the cleanup ran while Cancel() was still running its callbacks");
    }

    /// <summary>
    /// An exit paused between its decrement and its termination check must not clean up from a
    /// stale zero: while it is paused, another compile enters and the pipeline terminates. The
    /// cleanup must wait for that compile to leave. Negative control: the same sequence with the
    /// second compile leaving does clean, exactly once.
    /// </summary>
    [Fact]
    public void BakeLifetime_ExitPausedAfterItsDecrement_DoesNotCleanUnderACompileThatEnteredMeanwhile()
    {
        var cleaned = 0;
        var paused = false;
        var secondEntered = false;
        TreeBake.BakeLifetime? lifetime = null;
        lifetime = new TreeBake.BakeLifetime(new CancellationTokenSource(), () => cleaned++, () =>
        {
            if (paused)
                return;
            paused = true; // the first exit has decremented to zero and is paused here
            secondEntered = lifetime!.TryEnter();
            lifetime.Terminate();
        });
        Assert.True(lifetime.TryEnter());

        lifetime.Exit(); // resumes after the second compile entered and termination ran

        Assert.True(secondEntered, "the second compile entered while the first exit was paused");
        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.Equal(0, cleaned); // the second compile is inside — its resolver and directory must survive

        lifetime.Exit();
        Assert.Equal(1, cleaned);
    }

    /// <summary>
    /// Work run through the lifetime (the fold that writes bundles from the work directory) keeps
    /// the resources alive across a termination that happens while it runs, and is refused once
    /// the pipeline has terminated.
    /// </summary>
    [Fact]
    public void BakeLifetime_Run_HoldsCleanupUntilTheBodyReturned_AndRefusesAfterTermination()
    {
        var cleaned = 0;
        var lifetime = new TreeBake.BakeLifetime(new CancellationTokenSource(), () => cleaned++);

        var seenInside = lifetime.Run(() =>
        {
            lifetime.Terminate(); // an unsubscription while the fold is writing bundles
            return cleaned;
        });

        Assert.Equal(0, seenInside);
        Assert.Equal(1, cleaned);
        Assert.Throws<OperationCanceledException>(() => lifetime.Run(() => 0));
        Assert.Equal(1, cleaned);
    }

    // ── 2. byte-identical output ──────────────────────────────────────────────────────────────

    private const string PackageIndexJson =
        """{"$type":"MeshNode","id":"Ctrl","namespace":"","path":"Ctrl","mainNode":"Ctrl","name":"Ctrl","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Parallel bake control."}}""";

    private static string NodeTypeJson(string name, string? sourcesJson = null) =>
        $$$"""{"$type":"MeshNode","id":"{{{name}}}","namespace":"Ctrl","path":"Ctrl/{{{name}}}","mainNode":"Ctrl/{{{name}}}","name":"{{{name}}}","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"{{{name}}}",{{{(sourcesJson is null ? "" : $"\"sources\":{sourcesJson},")}}}"configuration":"config => config.WithContentType<{{{name}}}>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private static string Record(string name, string body = "") =>
        $$"""
        /// <summary>The {{name}} record.</summary>
        public record {{name}}
        {
            /// <summary>The name.</summary>
            public string Name { get; init; } = string.Empty;
        {{body}}
        }
        """;

    /// <summary>
    /// Eight independent leaves, a type that compiles a sibling's source in (ordered after it), and
    /// a type that does not compile — every shape a fold could reorder or drop.
    /// </summary>
    private static void WriteTree(string repo, string leafSevenBody = "")
    {
        Write(repo, "Ctrl/index.json", PackageIndexJson);
        for (var i = 0; i < 8; i++)
        {
            var name = $"Leaf{i}";
            Write(repo, $"Ctrl/{name}.json", NodeTypeJson(name));
            Write(repo, $"Ctrl/{name}/Source/{name}.cs", Record(name, i == 7 ? leafSevenBody : ""));
        }
        Write(repo, "Ctrl/Base.json", NodeTypeJson("Base"));
        Write(repo, "Ctrl/Base/Source/Base.cs", Record("Base"));
        Write(repo, "Ctrl/Uses.json",
            NodeTypeJson("Uses", """["namespace:Source scope:subtree","shared=@Ctrl/Base/Source"]"""));
        Write(repo, "Ctrl/Uses/Source/Uses.cs", Record("Uses",
            """
                /// <summary>The base it uses.</summary>
                public Base? Inner { get; init; }
            """));
        Write(repo, "Ctrl/Broken.json", NodeTypeJson("Broken"));
        Write(repo, "Ctrl/Broken/Source/Broken.cs", "public record Broken { this does not compile }");
    }

    [Fact(Timeout = 600_000)]
    public void AParallelBakeWritesTheSerialBakesBytes_AndADifferentSourceIsSeen()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var repo = TempDirectory("mw-par-repo");
        var changedRepo = TempDirectory("mw-par-repo-changed");
        var serialOut = TempDirectory("mw-par-serial");
        var serialAgainOut = TempDirectory("mw-par-serial-again");
        var parallelOut = TempDirectory("mw-par-parallel");
        var changedOut = TempDirectory("mw-par-changed");
        try
        {
            WriteTree(repo);
            WriteTree(changedRepo, leafSevenBody: "    /// <summary>A field only this tree has.</summary>\n    public int Extra { get; init; }");

            var serial = Bake(repo, serialOut, maxParallel: 1);
            var serialAgain = Bake(repo, serialAgainOut, maxParallel: 1);
            var parallel = Bake(repo, parallelOut, maxParallel: 6);

            // THE BASELINE: two SERIAL bakes of one tree agree once the per-emit fields are masked.
            // That is the strongest equality the serial bake itself offers, so it is the bar.
            var serialDrift = DifferingFiles(serialOut, serialAgainOut);
            Assert.True(serialDrift.IsEmpty,
                "two serial bakes differ beyond the per-emit fields: " + string.Join(", ", serialDrift));
            Assert.Equal(Verdicts(serial.Report), Verdicts(serialAgain.Report));

            // The bake really happened, with every shape present: 10 types compiled, one RED.
            Assert.Null(serial.Report.FatalError);
            Assert.Equal(11, serial.Report.Types.Length);
            Assert.Equal(["Ctrl/Broken"], serial.Report.Types.Where(t => !t.Success).Select(t => t.NodePath));
            Assert.Contains("max-parallel=6", parallel.Log);

            // Verdicts, in order.
            Assert.Equal(Verdicts(serial.Report), Verdicts(parallel.Report));
            Assert.Equal(serial.Report.Warnings.Pairs, parallel.Report.Warnings.Pairs);
            Assert.Equal(serial.Report.Warnings.CompiledTypes, parallel.Report.Warnings.CompiledTypes);
            Assert.Equal(serial.Report.Warnings.Occurrences, parallel.Report.Warnings.Occurrences);
            // Every log line but the one that reports the wall time, in order.
            Assert.Equal(LogLines(serial.Log, serialOut), LogLines(parallel.Log, parallelOut));
            // Every output byte, the per-emit fields masked exactly as for the serial baseline.
            var differences = DifferingFiles(serialOut, parallelOut);
            Assert.True(differences.IsEmpty,
                "the parallel bake wrote different output: " + string.Join(", ", differences));

            // NEGATIVE CONTROL — a bake whose Leaf7 source differs. The comparison above must SEE
            // that, in that bundle; a comparison that cannot is what "identical" would otherwise mean.
            Bake(changedRepo, changedOut, maxParallel: 6);
            var changed = DifferingFiles(serialOut, changedOut);
            output.WriteLine("changed tree differs in: " + string.Join(", ", changed));
            Assert.Contains("Ctrl.zip!meshweaver/assemblies/Ctrl/Leaf7.dll", changed);
            Assert.DoesNotContain("Ctrl.zip!meshweaver/assemblies/Ctrl/Leaf6.dll", changed);
        }
        finally
        {
            foreach (var d in new[] { repo, changedRepo, serialOut, serialAgainOut, parallelOut, changedOut })
                Cleanup(d);
        }
    }

    /// <summary>
    /// Two types whose SANITIZED names collide — <c>Ctrl/A_B</c> and <c>Ctrl/A/B</c> both become
    /// <c>Ctrl_A_B</c> — each ship their OWN assembly. The bundle entry reads its bytes back from
    /// the compile's output path when the bundle is written, so a shared directory handed both
    /// entries whichever DLL was written last (serially), or two half-written ones (in parallel).
    /// Each DLL must define its own record and not the other's — at both parallelisms.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public void TypesWhoseSanitizedNamesCollide_EachShipTheirOwnAssembly()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var repo = TempDirectory("mw-par-collide");
        var serialOut = TempDirectory("mw-par-collide-serial");
        var parallelOut = TempDirectory("mw-par-collide-parallel");
        try
        {
            Write(repo, "Ctrl/index.json", PackageIndexJson);
            Write(repo, "Ctrl/A_B.json", NodeTypeJson("A_B").Replace("WithContentType<A_B>", "WithContentType<Underscored>"));
            Write(repo, "Ctrl/A_B/Source/Underscored.cs", Record("Underscored"));
            Write(repo, "Ctrl/A/B.json",
                $$$"""{"$type":"MeshNode","id":"B","namespace":"Ctrl/A","path":"Ctrl/A/B","mainNode":"Ctrl/A/B","name":"B","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"B","configuration":"config => config.WithContentType<Nested>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""");
            Write(repo, "Ctrl/A/B/Source/Nested.cs", Record("Nested"));

            foreach (var (outDir, parallel) in new[] { (serialOut, 1), (parallelOut, 4) })
            {
                var run = Bake(repo, outDir, parallel);
                Assert.Null(run.Report.FatalError);
                Assert.Equal(["Ctrl/A/B", "Ctrl/A_B"], run.Report.Types.Where(t => t.Success).Select(t => t.NodePath));
                var entries = ZipEntries(File.ReadAllBytes(
                    Directory.EnumerateFiles(outDir, "*.zip", SearchOption.AllDirectories).Single()));
                Assert.Equal(["Underscored"], RecordsIn(entries.Single(e => e.Name.EndsWith("/Ctrl/A_B.dll", StringComparison.Ordinal)).Bytes));
                Assert.Equal(["Nested"], RecordsIn(entries.Single(e => e.Name.EndsWith("/Ctrl/A/B.dll", StringComparison.Ordinal)).Bytes));
            }
        }
        finally
        {
            foreach (var d in new[] { repo, serialOut, parallelOut })
                Cleanup(d);
        }
    }

    /// <summary>The names of the test records (<see cref="Record"/>) an assembly defines.</summary>
    private static ImmutableArray<string> RecordsIn(byte[] dll)
    {
        using var pe = new PEReader(new MemoryStream(dll));
        var metadata = pe.GetMetadataReader();
        return
        [
            .. metadata.TypeDefinitions
                .Select(h => metadata.GetString(metadata.GetTypeDefinition(h).Name))
                .Where(n => n is "Underscored" or "Nested")
                .Order(StringComparer.Ordinal),
        ];
    }

    private sealed record BakeRun(TreeBake.Report Report, string Log);

    private BakeRun Bake(string repo, string outDir, int maxParallel)
    {
        var log = new StringWriter();
        var report = TreeBake.Run(new TreeBake.Options
        {
            RepoRoot = repo,
            OutputDirectory = outDir,
            SourceSha = "deadbeef",
            Output = log,
            MaxParallel = maxParallel,
        });
        output.WriteLine($"── max-parallel={maxParallel}\n{log}");
        return new BakeRun(report, log.ToString());
    }

    private static ImmutableArray<string> Verdicts(TreeBake.Report report) =>
    [
        .. report.Types.Select(t =>
            $"{t.NodePath}|{t.Package}|{t.Error}|{string.Join(",", t.MatchedSourcePaths)}|"
            + $"{string.Join(",", t.ResolvedIncludePaths)}|{t.Warnings.Length}|{t.DependencyEdges is not null}"),
    ];

    private static ImmutableArray<string> LogLines(string log, string outDir) =>
    [
        .. log.Split('\n')
            .Where(l => !l.StartsWith("bake: compiled ", StringComparison.Ordinal))
            .Select(l => l.Replace(outDir, "<out>", StringComparison.Ordinal)),
    ];

    /// <summary>
    /// Every output file — and every entry inside a bundle — whose bytes differ between two bakes,
    /// AFTER masking exactly the fields the emit itself makes per-run (<see cref="Normalize"/>).
    /// </summary>
    private static ImmutableArray<string> DifferingFiles(string left, string right)
    {
        static ImmutableSortedSet<string> Files(string root) =>
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f)),
        ];
        var l = Files(left);
        var r = Files(right);
        Assert.NotEmpty(l);
        var differing = ImmutableArray.CreateBuilder<string>();
        differing.AddRange(l.SymmetricExcept(r).Select(f => f + " (present on one side only)"));
        foreach (var file in l.Intersect(r))
        {
            var a = File.ReadAllBytes(Path.Combine(left, file));
            var b = File.ReadAllBytes(Path.Combine(right, file));
            if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (!Normalize(file, a).SequenceEqual(Normalize(file, b)))
                    differing.Add(file);
                continue;
            }
            var entriesA = ZipEntries(a);
            var entriesB = ZipEntries(b);
            if (!entriesA.Select(e => e.Name).SequenceEqual(entriesB.Select(e => e.Name)))
                differing.Add(file + " (entry list)");
            foreach (var (name, bytes) in entriesA)
            {
                var other = entriesB.FirstOrDefault(e => e.Name == name);
                if (other.Bytes is null || !Normalize(name, bytes).SequenceEqual(Normalize(name, other.Bytes)))
                    differing.Add($"{file}!{name}");
            }
        }
        return differing.ToImmutable();
    }

    private static ImmutableArray<(string Name, byte[] Bytes)> ZipEntries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return
        [
            .. archive.Entries.Select(e =>
            {
                using var stream = e.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return (e.FullName, buffer.ToArray());
            }),
        ];
    }

    /// <summary>
    /// 🚨 The serial bake is not reproducible byte-for-byte on its own: the emit is NOT
    /// deterministic, so every compile mints a fresh MVID (and with it a PE timestamp, a PDB id and
    /// the <c>buildMvid</c> the manifest and the edge file record), and the bundle's zip entries
    /// carry their write time. Two SERIAL bakes of one tree differ in exactly those fields, so this
    /// masks exactly those — the MVID wherever its bytes occur, the COFF and debug-directory
    /// timestamps and the debug-directory payloads (the CodeView PDB id), the portable PDB's id,
    /// and the recorded <c>buildMvid</c> values — and compares everything else byte for byte. The
    /// negative control proves the masking leaves a real change visible.
    /// </summary>
    private static byte[] Normalize(string name, byte[] bytes)
    {
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return NormalizeAssembly(bytes);
        if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            return NormalizePdb(bytes);
        return Encoding.UTF8.GetBytes(BuildMvid().Replace(Encoding.UTF8.GetString(bytes), "\"buildMvid\": \"<mvid>\""));
    }

    private static byte[] NormalizeAssembly(byte[] dll)
    {
        var copy = (byte[])dll.Clone();
        using var pe = new PEReader(new MemoryStream(dll));
        var metadata = pe.GetMetadataReader();
        ZeroEvery(copy, metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToByteArray());
        Array.Clear(copy, pe.PEHeaders.CoffHeaderStartOffset + 4, 4); // COFF TimeDateStamp
        if (pe.PEHeaders.TryGetDirectoryOffset(pe.PEHeaders.PEHeader!.DebugTableDirectory, out var table))
        {
            var entries = pe.ReadDebugDirectory();
            for (var i = 0; i < entries.Length; i++)
            {
                Array.Clear(copy, table + (i * 28) + 4, 4); // the entry's TimeDateStamp
                if (entries[i].DataSize > 0)
                    Array.Clear(copy, entries[i].DataPointer, entries[i].DataSize);
            }
        }
        return copy;
    }

    private static byte[] NormalizePdb(byte[] pdb)
    {
        var copy = (byte[])pdb.Clone();
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb));
        ZeroEvery(copy, provider.GetMetadataReader().DebugMetadataHeader!.Id.ToArray());
        return copy;
    }

    private static void ZeroEvery(byte[] haystack, byte[] needle)
    {
        for (var at = haystack.AsSpan().IndexOf(needle); at >= 0;)
        {
            Array.Clear(haystack, at, needle.Length);
            var next = haystack.AsSpan(at + needle.Length).IndexOf(needle);
            at = next < 0 ? -1 : at + needle.Length + next;
        }
    }

    [GeneratedRegex("\"buildMvid\":\\s*\"[0-9a-f]{32}\"")]
    private static partial Regex BuildMvid();

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory costs disk, never correctness.
        }
    }
}
