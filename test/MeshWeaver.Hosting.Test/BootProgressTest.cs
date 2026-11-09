using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The boot progress a fresh instance shows on its boot page and in the memex-local terminal
/// (maintainer, 2026-11-09: "can we bring better progress bar?"), and the module phase that no
/// longer lands one module at a time.
///
/// <para>Pins: the dependency LEVELS a module wave runs in (a level is a barrier, every satisfiable
/// edge holds, a cycle still levels every package); the wave itself (never more than the degree in
/// flight, never a level before the one below it is decided); the recorder's facts and the ONE
/// renderer's derived values (ETA from the phase's own measured throughput, null before the first
/// unit; the running modules named in the headline; the module list dropped once ready); the
/// previous boot read back across a restart; and the memo that stops a module-only bundle being
/// downloaded twice.</para>
/// </summary>
public class BootProgressTest
{
    private static PackageManifest Pkg(string id, params string[] requires) =>
        new() { Id = id, Requires = requires.ToImmutableList() };

    // ── Dependency levels ───────────────────────────────────────────────────────────────────

    [Fact]
    public void LevelsPutEveryDependencyStrictlyBelowItsDependent()
    {
        // AI ← OpenAI, AI ← Anthropic, Store ← Edu ← EduExtras, and an edge through Chat
        // (content only here) that must still order Teams after AI.
        var packages = new[]
        {
            Pkg("Teams", "Chat@^1"), Pkg("OpenAI", "AI@^1.40.0"), Pkg("Chat", "AI"),
            Pkg("AI"), Pkg("Anthropic", "AI"), Pkg("Store"), Pkg("Edu", "Store"), Pkg("EduExtras", "Edu"),
            Pkg("Maps", "NotInTheSelection@^2"),
        };

        var levels = PackageDependencyGraph.DependencyLevels(packages);

        Assert.Equal(0, levels["AI"]);
        Assert.Equal(0, levels["Store"]);
        Assert.Equal(0, levels["Maps"]); // a dependency outside the selection is ignored
        Assert.Equal(1, levels["OpenAI"]);
        Assert.Equal(1, levels["Anthropic"]);
        Assert.Equal(1, levels["Chat"]);
        Assert.Equal(2, levels["Teams"]);
        Assert.Equal(1, levels["Edu"]);
        Assert.Equal(2, levels["EduExtras"]);
        foreach (var p in packages)
            foreach (var dep in p.Requires.Select(PackageDependencyGraph.DependencyId).Where(levels.ContainsKey))
                Assert.True(levels[dep] < levels[p.Id], $"{dep} must level below {p.Id}");
    }

    [Fact]
    public void ACycleStillLevelsEveryPackageOnce()
    {
        var levels = PackageDependencyGraph.DependencyLevels([Pkg("A", "B"), Pkg("B", "A"), Pkg("C", "A")]);

        Assert.Equal(3, levels.Count);
        Assert.True(levels["C"] > levels["A"], "the edge from outside the cycle still holds");
    }

    // ── The wave ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWaveNeverExceedsItsDegreeAndNeverStartsALevelEarly()
    {
        // Every item's work is a Subject the test completes by hand, so "in flight" is exact.
        var items = new[] { ("a0", 0), ("b0", 0), ("c0", 0), ("d0", 0), ("e0", 0), ("a1", 1), ("b1", 1) };
        var pending = new Dictionary<string, Subject<string>>();
        var started = new List<string>();
        var results = new List<string>();
        var done = new AsyncSubject<bool>();

        using var _ = LevelledWave.Run(items, i => i.Item2, degree: 2, i =>
            {
                started.Add(i.Item1);
                var s = new Subject<string>();
                pending[i.Item1] = s;
                return s;
            })
            .Subscribe(results.Add, () => { done.OnNext(true); done.OnCompleted(); });

        Assert.Equal(["a0", "b0"], started);                 // degree 2: exactly two in flight
        Complete(pending, "a0");
        Assert.Equal(["a0", "b0", "c0"], started);           // a slot freed → the next of the SAME level
        Complete(pending, "b0");
        Complete(pending, "c0");
        Complete(pending, "d0");
        Assert.DoesNotContain("a1", started);                // e0 still running: level 1 waits
        Complete(pending, "e0");
        Assert.Equal(["a1", "b1"], started.Skip(5));         // the barrier lifted, both of level 1 start
        Complete(pending, "b1");
        Complete(pending, "a1");

        Assert.True(await done.FirstAsync().Await(TestContext.Current.CancellationToken));
        Assert.Equal(["a0", "b0", "c0", "d0", "e0", "b1", "a1"], results);
    }

    [Fact]
    public async Task ADegreeOfOneIsTheSequentialWave()
    {
        var order = await LevelledWave.Run(new[] { ("x", 1), ("y", 0), ("z", 0) }, i => i.Item2, 1,
                i => Observable.Return(i.Item1))
            .ToList().Await(TestContext.Current.CancellationToken);

        Assert.Equal(["y", "z", "x"], order);
    }

    private static void Complete(Dictionary<string, Subject<string>> pending, string id)
    {
        pending[id].OnNext(id);
        pending[id].OnCompleted();
    }

    // ── The recorder and the report ─────────────────────────────────────────────────────────

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheEtaComesFromThePhasesOwnMeasuredRateAndIsNullBeforeTheFirstUnit()
    {
        var clock = new ManualClock(T0);
        using var progress = new BootProgress(null, null, null, clock, "moench", T0);
        progress.ModulesPlanned([new BootModuleStep("Mcp", "MeshWeaver.Mcp"), new BootModuleStep("OgCard", "MeshWeaver.OgCard"),
            new BootModuleStep("Maps", "MeshWeaver.Maps"), new BootModuleStep("Voice", "MeshWeaver.Voice")]);
        progress.PhaseStarted(BootPhaseIds.Modules, "Landing modules", total: 4, concurrency: 2);

        clock.Now = T0.AddSeconds(5);
        var before = BootProgressReport.From(progress.Current, clock.Now, ready: false);
        Assert.Null(before.EtaSeconds); // no unit finished: no rate, no guess

        progress.ModuleStarted("Mcp");
        progress.BundleDownloaded("Mcp", "1.7.14", 19_970_639, TimeSpan.FromSeconds(18.3));
        progress.ModuleStarted("OgCard");
        clock.Now = T0.AddSeconds(20);
        progress.ModuleEnded("Mcp", BootModuleStates.Landed);
        progress.PhaseStep(BootPhaseIds.Modules);

        var report = BootProgressReport.From(progress.Current, clock.Now, ready: false);
        Assert.Equal(BootPhaseIds.Modules, report.Phase);
        Assert.Equal(60, report.EtaSeconds);  // 3 left × 20 s per unit measured
        var mcp = report.Modules.Single(m => m.Package == "Mcp");
        Assert.Equal((BootModuleStates.Landed, "1.7.14", 19_970_639L, 18.3), (mcp.State, mcp.Version, mcp.Bytes, mcp.DownloadSeconds));
        Assert.Equal("Landing modules — 1 of 4 (OgCard)", report.Headline);
        Assert.Equal(20, report.Phases.Single().Seconds);
    }

    [Fact]
    public void AFailedModuleIsNamedAndReadinessDropsTheModuleList()
    {
        var clock = new ManualClock(T0);
        using var progress = new BootProgress(null, null, null, clock, "moench", T0);
        progress.ModulesPlanned([new BootModuleStep("Notifications", "MeshWeaver.Notifications")]);
        progress.PhaseStarted(BootPhaseIds.Modules, "Landing modules", total: 1);
        progress.ModuleEnded("Notifications", BootModuleStates.Failed, error: "HTTP 503 from the registry");
        progress.PhaseStep(BootPhaseIds.Modules, failed: true);
        progress.PhaseEnded(BootPhaseIds.Modules, BootStepStates.Failed, "0 landed, 1 did not land");

        var waiting = BootProgressReport.From(progress.Current, clock.Now, ready: false,
            [new BootCheck("view_packs", "Unhealthy", "the entity-views pack did not register")]);
        Assert.Equal(["Notifications: HTTP 503 from the registry"], waiting.Errors);
        Assert.Equal(1, waiting.Phases.Single().Failed);
        Assert.Equal("Waiting for readiness: view_packs", waiting.Headline);
        Assert.Single(waiting.Modules);

        var ready = BootProgressReport.From(progress.Current, clock.Now, ready: true);
        Assert.Equal(("ready", "Ready"), (ready.Phase, ready.Headline));
        Assert.Empty(ready.Modules);
        Assert.Null(ready.EtaSeconds);
    }

    [Fact]
    public void ErrorsAreBounded()
    {
        using var progress = new BootProgress();
        for (var i = 0; i < BootProgress.MaxErrors + 5; i++)
            progress.Error($"e{i}");

        Assert.Equal(BootProgress.MaxErrors, progress.Current.Errors.Count);
        Assert.Equal($"e{BootProgress.MaxErrors + 4}", progress.Current.Errors[^1]);
    }

    [Fact]
    public void TheReportIsCamelCaseJsonWithoutNulls()
    {
        using var progress = new BootProgress(null, null, null, new ManualClock(T0), "moench", T0);
        progress.PhaseStarted(BootPhaseIds.Resolve, "Resolving the module set");

        var json = JsonSerializer.Serialize(BootProgressReport.From(progress.Current, T0, false), BootProgressReport.JsonOptions);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("resolve", doc.RootElement.GetProperty("phase").GetString());
        Assert.False(doc.RootElement.TryGetProperty("etaSeconds", out _));
        Assert.Equal("running", doc.RootElement.GetProperty("phases")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task ThePreviousBootOfTheSameHostIsReadBackAfterARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-boot-" + Guid.NewGuid().ToString("N")[..8]);
        var pool = IoPool.Unbounded;
        try
        {
            using (var first = new BootProgress(dir, pool, null, new ManualClock(T0), "moench-portal-1", T0))
            {
                first.ModulesPlanned([new BootModuleStep("AI", "MeshWeaver.AI")]);
                first.PhaseStarted(BootPhaseIds.Modules, "Landing modules", total: 1);
                first.ModuleEnded("AI", BootModuleStates.Landed, "1.51.3");
                first.PhaseStep(BootPhaseIds.Modules);
                first.PhaseEnded(BootPhaseIds.Modules, BootStepStates.Done);
                first.PhaseStarted(BootPhaseIds.Activate, "Restarting to activate the landed modules");
                var file = Path.Combine(dir, "moench-portal-1.json");
                await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                    .Select(_ => File.Exists(file) && File.ReadAllText(file).Contains("activate"))
                    .Where(written => written).FirstAsync().Timeout(TimeSpan.FromSeconds(15))
                    .Await(TestContext.Current.CancellationToken);
            }

            using var second = new BootProgress(dir, pool, null, new ManualClock(T0.AddMinutes(9)), "moench-portal-1", T0.AddMinutes(9));
            var previous = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Select(_ => second.Current.PreviousBoot)
                .Where(p => p is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(15))
                .Await(TestContext.Current.CancellationToken);

            Assert.Equal(T0, previous!.ProcessStartedAt);
            var report = BootProgressReport.From(second.Current, T0.AddMinutes(9), ready: false);
            Assert.Equal("Restarted to activate 1 landed module(s)", report.PreviousBoot!.Headline);
            Assert.Null(report.PreviousBoot.PreviousBoot);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ── The download memo ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AModuleOnlyBundleIsRememberedForItsVersionAndRegistryOnlyAndOnlyForAWhile()
    {
        var clock = new ManualClock(T0);
        var memo = new BundleOfferingMemo(clock);
        memo.Remember("https://memex.meshweaver.cloud/", "Mcp", "1.7.14", "module 'MeshWeaver.Mcp'");

        Assert.Equal("module 'MeshWeaver.Mcp'", memo.NothingToAdopt("https://memex.meshweaver.cloud", "mcp", "1.7.14"));
        Assert.Null(memo.NothingToAdopt("https://memex.meshweaver.cloud", "Mcp", "1.7.15"));
        Assert.Null(memo.NothingToAdopt("https://other.example.com", "Mcp", "1.7.14"));
        clock.Now = T0 + BundleOfferingMemo.Lifetime;
        Assert.Null(memo.NothingToAdopt("https://memex.meshweaver.cloud", "Mcp", "1.7.14"));
    }

    [Fact]
    public void AHostNameBecomesASafeFileName() =>
        Assert.Equal("memex-portal_x.y.json", BootProgress.FileName("memex-portal/x.y"));
}
