using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Testing.InMesh;

/// <summary>
/// The STREAMING half of the runner: what a <c>Tests</c> area shows while its cases run.
///
/// <para>🚨 A Tests area used to emit ONE frame, at the end. A case that called an external service
/// live, or one that hung, therefore left the page on "Rendering …" for its whole budget — with no
/// way to tell a slow case from a stuck one, and nothing on screen to say which case it was. Now the
/// area renders every case as pending the moment it opens, then the running case with its elapsed
/// time (ticking) and every output line it has written so far, and each finished case with its
/// verdict. Only the LAST frame is the verdict.</para>
///
/// <para>🚨 Progress frames carry <see cref="AreaFrameClassifier.TestsRunningId"/> so a consumer that
/// classifies the first frame it can (the plugin gate) keeps waiting for the verdict. And they are
/// written so that even a consumer that predates the id cannot mistake one for a verdict: a case
/// that has passed SO FAR shows ✔, one that failed ✖, and the title counts cases "done" — never
/// the ✅ / ❌ glyphs or the "N/M passed" sentence a verdict carries.</para>
/// </summary>
public static partial class MeshTestRunner
{
    /// <summary>What a pending row's result column shows.</summary>
    public const string PendingResult = "⏳";

    /// <summary>What the running row's result column shows.</summary>
    public const string RunningResult = "▶";

    /// <summary>The prefix of every verdict a case earns by exceeding its bound.</summary>
    public const string TimedOutPrefix = "timed out: ";

    /// <summary>How often a progress frame is emitted while a case runs (its elapsed time ticks).</summary>
    public static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>One frame of a run: every case's current row, and whether this is the verdict.</summary>
    /// <param name="Cases">Every case of the run, in run order — pending, running or finished.</param>
    /// <param name="Complete">True on the one final snapshot, after the last case finished.</param>
    /// <param name="Elapsed">Time since the run started.</param>
    public sealed record TestRunSnapshot(IReadOnlyList<CaseResult> Cases, bool Complete, TimeSpan Elapsed)
    {
        /// <summary>Cases that have a verdict (passed, failed or skipped).</summary>
        public int Done => Cases.Count(c => !IsUnfinished(c));
    }

    /// <summary>A row of the rendered table (the data the grid binds).</summary>
    /// <param name="Class">The test class (or suite, for listed cases).</param>
    /// <param name="Case">The case's display name.</param>
    /// <param name="Result">The status glyph and word.</param>
    /// <param name="Time">Elapsed, in seconds.</param>
    /// <param name="Output">The failure message and the case's output lines.</param>
    public sealed record CaseRow(string Class, string Case, string Result, string Time, string Output);

    /// <summary>The column titles, resolved ONCE in the viewer's language while the render scope still carries it.</summary>
    /// <param name="Class">Title of the class column.</param>
    /// <param name="Case">Title of the case column.</param>
    /// <param name="Result">Title of the result column.</param>
    /// <param name="Time">Title of the time column.</param>
    /// <param name="Output">Title of the output column.</param>
    /// <param name="ProgressTitle">The progress title format: suite, done, total, seconds.</param>
    public sealed record ColumnTitles(string Class, string Case, string Result, string Time, string Output, string ProgressTitle)
    {
        /// <summary>The English titles, for a render with no viewer.</summary>
        public static readonly ColumnTitles English = new("Class", "Case", "Result", "Time", "Output", "{0} tests — {1} of {2} done, running for {3}s");

        /// <summary>The titles in the viewer's language.</summary>
        public static ColumnTitles For(LayoutAreaHost host) => new(
            host.Localize("tests.column.class"),
            host.Localize("tests.column.case"),
            host.Localize("tests.column.result"),
            host.Localize("tests.column.time"),
            host.Localize("tests.column.output"),
            host.Localize("tests.progress", "{0}", "{1}", "{2}", "{3}"));
    }

    /// <summary>
    /// Runs the classes' cases one after another and emits a snapshot of EVERY case: first all
    /// pending, then — at most once per <paramref name="interval"/> — the running case's elapsed
    /// time and output so far, and finally the complete verdict (<see cref="TestRunSnapshot.Complete"/>).
    /// </summary>
    /// <param name="host">The area host; null runs the classes that need no mesh.</param>
    /// <param name="classes">The test classes.</param>
    /// <param name="deadline">The per-case bound when a case declares none.</param>
    /// <param name="pool">The pool the cases run on; null resolves the mesh's Tests pool.</param>
    /// <param name="interval">How often a running case's row is refreshed; null = <see cref="DefaultProgressInterval"/>.</param>
    public static IObservable<TestRunSnapshot> Progress(LayoutAreaHost? host, IEnumerable<Type> classes, TimeSpan deadline, IIoPool? pool = null, TimeSpan? interval = null)
    {
        var list = classes.ToList();
        var plan = list.SelectMany(cls => Cases(cls).Select(c => (cls.Name, c.Name))).ToImmutableArray();
        return Track(plan, (started, line) => Run(host, list, deadline, pool, started, line), interval ?? DefaultProgressInterval);
    }

    /// <summary>
    /// Runs listed cases one after another and emits snapshots exactly as the class-based overload does.
    /// </summary>
    /// <param name="host">The area host; null runs without a mesh (synchronous cases on an unbounded pool).</param>
    /// <param name="suite">What the class column says.</param>
    /// <param name="cases">The cases, in run order.</param>
    /// <param name="deadline">The per-case bound when a case declares none.</param>
    /// <param name="pool">The pool synchronous cases run on; null resolves the mesh's Tests pool.</param>
    /// <param name="interval">How often a running case's row is refreshed; null = <see cref="DefaultProgressInterval"/>.</param>
    public static IObservable<TestRunSnapshot> Progress(LayoutAreaHost? host, string suite, IReadOnlyList<MeshTestCase> cases, TimeSpan deadline, IIoPool? pool = null, TimeSpan? interval = null)
    {
        var plan = cases.Select(c => (suite, c.Name)).ToImmutableArray();
        var resolved = pool ?? host?.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Tests) ?? IoPool.Unbounded;
        return Track(plan,
            (started, line) => cases.Select(c => RunListed(suite, c, deadline, resolved, started, line)).Concat(),
            interval ?? DefaultProgressInterval);
    }

    // One listed case: its clock, its output, its bound — and every outcome, including "completed
    // without ever emitting", turned into a row rather than into silence.
    private static IObservable<CaseResult> RunListed(string suite, MeshTestCase c, TimeSpan deadline, IIoPool pool, Action onStarted, Action<string> onLine) =>
        Observable.Defer(() =>
        {
            var bound = c.Timeout ?? deadline;
            var started = DateTimeOffset.UtcNow;
            var output = new OutputLines(onLine);
            onStarted();
            var work = c.Synchronous is { } body
                ? pool.InvokeBlocking(_ => { body(output.Add); return Unit.Default; })
                : Observable.Defer(() => c.Reactive is { } live
                    ? live(output.Add)
                    : Observable.Throw<Unit>(new InvalidOperationException($"the case '{c.Name}' has no body")));
            CaseResult Verdict(string? failure) => failure is null
                ? new CaseResult(suite, c.Name, "✅ pass", output.Joined, DateTimeOffset.UtcNow - started)
                : new CaseResult(suite, c.Name, "❌ FAIL", failure + (output.Joined.Length > 0 ? " · " + output.Joined : ""), DateTimeOffset.UtcNow - started);
            return work
                .Select(_ => (string?)null)
                .Take(1)
                .DefaultIfEmpty("the case completed without an outcome")
                .Timeout(bound, Observable.Return<string?>($"{TimedOutPrefix}no verdict within {bound.TotalSeconds:F0}s"))
                .Catch<string?, Exception>(ex => Observable.Return<string?>(Unwrap(ex).Message))
                .Select(Verdict);
        });

    // A case's output lines: kept for its verdict, and forwarded to the progress frame as written.
    // Lines may arrive from any thread the case happens to run on, hence the interlocked swap.
    private sealed class OutputLines(Action<string> forward)
    {
        private ImmutableList<string> lines = ImmutableList<string>.Empty;

        public void Add(string line)
        {
            ImmutableInterlocked.Update(ref lines, l => l.Add(line));
            forward(line);
        }

        public string Joined => string.Join(" · ", lines);
    }

    // ———————————————————————————————————————————————————————— the snapshot fold

    private abstract record RunEvent;
    private sealed record StartedEvent : RunEvent;
    private sealed record LineEvent(string Text) : RunEvent;
    private sealed record FinishedEvent(CaseResult Result) : RunEvent;
    private sealed record TickEvent : RunEvent;
    private sealed record DoneEvent : RunEvent;

    private sealed record RunState(
        ImmutableArray<CaseResult> Rows,
        int Running,
        DateTimeOffset RunningSince,
        ImmutableList<string> RunningOutput,
        DateTimeOffset StartedAt,
        bool Dirty,
        bool Emit,
        bool Complete);

    private static IObservable<TestRunSnapshot> Track(
        ImmutableArray<(string Class, string Name)> plan,
        Func<Action, Action<string>, IObservable<CaseResult>> run,
        TimeSpan interval) =>
        Observable.Defer(() =>
        {
            // Started/line signals come from inside the cases (any thread), finished verdicts from the
            // run itself; Merge serialises them into ONE ordered fold, so no state here is shared.
            var signals = Subject.Synchronize(new Subject<RunEvent>());
            var now = DateTimeOffset.UtcNow;
            var initial = new RunState(
                [.. plan.Select(p => new CaseResult(p.Class, p.Name, PendingResult, "", TimeSpan.Zero))],
                -1, now, [], now, Dirty: false, Emit: true, Complete: false);
            var finished = run(() => signals.OnNext(new StartedEvent()), line => signals.OnNext(new LineEvent(line)))
                .Select(result => (RunEvent)new FinishedEvent(result));
            return finished
                .Publish(verdicts =>
                {
                    var over = verdicts.LastOrDefaultAsync();
                    return Observable.Merge(
                        verdicts,
                        signals.TakeUntil(over),
                        Observable.Interval(interval).Select(_ => (RunEvent)new TickEvent()).TakeUntil(over));
                })
                .Concat(Observable.Return<RunEvent>(new DoneEvent()))
                .Scan(initial, Apply)
                .StartWith(initial)
                .Where(state => state.Emit)
                .Select(state => new TestRunSnapshot(state.Rows, state.Complete, DateTimeOffset.UtcNow - state.StartedAt));
        });

    // Started, line and verdict only mark the state DIRTY; a tick (or the end) is what emits, so a
    // suite of fifty instant cases renders a handful of frames, not a hundred.
    private static RunState Apply(RunState state, RunEvent e)
    {
        var at = DateTimeOffset.UtcNow;
        switch (e)
        {
            case StartedEvent:
            {
                var index = FirstUnfinished(state.Rows);
                if (index < 0) return state with { Emit = false };
                var row = state.Rows[index] with { Result = RunningResult, Detail = "", Elapsed = TimeSpan.Zero };
                return state with { Rows = state.Rows.SetItem(index, row), Running = index, RunningSince = at, RunningOutput = [], Dirty = true, Emit = false };
            }
            case LineEvent line when state.Running >= 0:
            {
                var output = state.RunningOutput.Add(line.Text);
                var row = state.Rows[state.Running] with { Detail = string.Join(" · ", output) };
                return state with { Rows = state.Rows.SetItem(state.Running, row), RunningOutput = output, Dirty = true, Emit = false };
            }
            case FinishedEvent done:
            {
                var index = state.Running >= 0 ? state.Running : FirstUnfinished(state.Rows);
                if (index < 0) return state with { Emit = false };
                return state with { Rows = state.Rows.SetItem(index, done.Result), Running = -1, RunningOutput = [], Dirty = true, Emit = false };
            }
            case TickEvent:
            {
                if (state.Running < 0)
                    return state with { Emit = state.Dirty, Dirty = false };
                var row = state.Rows[state.Running] with { Elapsed = at - state.RunningSince };
                return state with { Rows = state.Rows.SetItem(state.Running, row), Emit = true, Dirty = false };
            }
            case DoneEvent:
                return state with { Complete = true, Emit = true, Dirty = false };
            default:
                return state with { Emit = false };
        }
    }

    private static int FirstUnfinished(ImmutableArray<CaseResult> rows)
    {
        for (var i = 0; i < rows.Length; i++)
            if (IsUnfinished(rows[i])) return i;
        return -1;
    }

    private static bool IsUnfinished(CaseResult row) =>
        row.Result is PendingResult or RunningResult;

    // ———————————————————————————————————————————————————————— rendering

    private static IObservable<UiControl?> Frames(LayoutAreaHost host, string suite, IObservable<TestRunSnapshot> snapshots)
    {
        // Resolved HERE, in the render scope that carries the viewer — the frames below are built on
        // timer and pool threads, where no AccessContext is set and every key would fall to English.
        var titles = ColumnTitles.For(host);
        return snapshots.Select(snapshot => (UiControl?)(snapshot.Complete
            ? Render(suite, snapshot.Cases, titles)
            : RenderProgress(suite, snapshot, titles)));
    }

    /// <summary>
    /// The verdict frame — the gate's contract: a title carrying "N/M passed" and a ✅/❌ table.
    /// </summary>
    /// <param name="suite">The suite name.</param>
    /// <param name="results">Every case's verdict.</param>
    /// <param name="titles">The column titles.</param>
    public static UiControl Render(string suite, IReadOnlyList<CaseResult> results, ColumnTitles titles) =>
        Controls.Stack.WithWidth("100%")
            .WithView(Controls.Title(Summary(suite, results), 2), "Title")
            .WithView(Grid(results.Select(r => Row(r, r.Result, r.Detail)), titles), "Cases");

    /// <summary>
    /// A progress frame: <see cref="AreaFrameClassifier.TestsRunningId"/>, a title that counts the
    /// cases DONE (never "passed"), and ✔ / ✖ for a finished case — the verdict glyphs are reserved
    /// for the verdict frame.
    /// </summary>
    /// <param name="suite">The suite name.</param>
    /// <param name="snapshot">The run so far.</param>
    /// <param name="titles">The column titles and progress title.</param>
    public static UiControl RenderProgress(string suite, TestRunSnapshot snapshot, ColumnTitles titles) =>
        Controls.Stack.WithWidth("100%")
            .WithView(Controls.Title(string.Format(System.Globalization.CultureInfo.InvariantCulture, titles.ProgressTitle,
                suite, snapshot.Done, snapshot.Cases.Count, (int)snapshot.Elapsed.TotalSeconds), 2), "Title")
            .WithView(Controls.Progress("", snapshot.Cases.Count == 0 ? 100 : snapshot.Done * 100 / snapshot.Cases.Count), "Progress")
            .WithView(Grid(snapshot.Cases.Select(r => Row(r, ProgressResult(r.Result), Neutral(r.Detail))), titles), "Cases")
            .WithId(AreaFrameClassifier.TestsRunningId);

    private static CaseRow Row(CaseResult r, string result, string detail) =>
        new(r.Class, r.Name, result, IsUnfinished(r) && r.Result == PendingResult ? "" : $"{r.Elapsed.TotalSeconds:0.0}s", detail);

    private static DataGridControl Grid(IEnumerable<CaseRow> rows, ColumnTitles titles) =>
        Controls.DataGrid(rows.ToImmutableArray())
            .WithColumn(
                new PropertyColumnControl<string> { Property = "class" }.WithTitle(titles.Class),
                new PropertyColumnControl<string> { Property = "case" }.WithTitle(titles.Case),
                new PropertyColumnControl<string> { Property = "result" }.WithTitle(titles.Result),
                new PropertyColumnControl<string> { Property = "time" }.WithTitle(titles.Time).WithAlign("end"),
                new PropertyColumnControl<string> { Property = "output" }.WithTitle(titles.Output));

    // A finished case in a PROGRESS frame: passed-so-far / failed-so-far, never the verdict glyphs.
    private static string ProgressResult(string result) =>
        result.StartsWith("✅", StringComparison.Ordinal) ? "✔"
        : result.StartsWith("❌", StringComparison.Ordinal) ? "✖"
        : result;

    // A case's own output may carry the verdict glyphs too (a nested report, a copied line); in a
    // progress frame they are neutralised for the same reason as the result column.
    private static string Neutral(string text) =>
        text.Replace("✅", "✔", StringComparison.Ordinal).Replace("❌", "✖", StringComparison.Ordinal);
}
