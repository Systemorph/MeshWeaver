using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

public partial class MeshOperations
{
    /// <summary>The default bound on a whole <see cref="RunTests"/> run.</summary>
    public const int DefaultRunTestsSeconds = 600;

    /// <summary>
    /// Runs a node's <c>Tests</c> area as an ACTIVITY and returns as soon as the activity exists:
    /// <c>{status:'Dispatched', activityPath}</c>. The activity (in the CALLER's own partition)
    /// gets one line per case as it starts, writes output and reaches its verdict, and ends
    /// <c>Succeeded</c> when every counted case passed, <c>Failed</c> otherwise — including when no
    /// verdict arrived within <paramref name="timeoutSeconds"/>, which names the case still running.
    ///
    /// <para>🚨 Why an activity and not a poll of <c>get @node/area/Tests</c>: rendering the area RUNS
    /// the suite, and a one-shot read opens a fresh subscription each time — measured on
    /// memex.meshweaver.cloud, two <c>get</c> reads 34 s apart each filed a new pair of the
    /// Maintenance suite's live request nodes, and the second answered with an OLD verdict frame
    /// while its own run had only just started. This keeps ONE subscription for the life of the run and turns its frames into a
    /// log anyone can poll without starting anything.</para>
    /// </summary>
    /// <param name="path">The node whose Tests area to run.</param>
    /// <param name="timeoutSeconds">The bound on the whole run.</param>
    public IObservable<string> RunTests(string path, int timeoutSeconds = DefaultRunTestsSeconds)
    {
        logger.LogInformation("RunTests called with path={Path}", path);
        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return(Json(new { status = "Error", message = "path is required" }));
        var resolvedPath = ResolvePath(path).Trim('/');
        var userId = ResolveCallerUserId();
        if (userId == WellKnownUsers.Anonymous)
            return Observable.Return(Json(new
            {
                status = "Error",
                path = resolvedPath,
                message = "run_tests needs a signed-in caller: the run's activity is written to the caller's own partition.",
            }));
        var budget = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600));
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var pathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();

        return Observable.Create<string>(observer =>
        {
            var answered = 0;
            void Answer(string json)
            {
                if (Interlocked.Exchange(ref answered, 1) != 0) return;
                observer.OnNext(json);
                observer.OnCompleted();
            }
            var caller = accessService?.Context ?? accessService?.CircuitContext;
            // The run OUTLIVES this call on purpose: the caller gets the activity path and polls it.
            // Its lifetime is the activity's — bounded by the budget below, cancellable through the
            // activity's own RequestedStatus, and tracked by ActivityRunner for teardown.
            hub.RunActivity(userId, ActivityCategory.TestRun,
                    new LogMessage($"Run the Tests area of {resolvedPath}", LogLevel.Information)
                        .WithKey("activity.tests.title", ("path", (object?)resolvedPath)),
                    ctx => WatchTestsArea(resolvedPath, caller, pathResolver, budget, ctx),
                    activityPath => Answer(Json(new { status = "Dispatched", path = resolvedPath, activityPath })))
                .Subscribe(
                    _ => { },
                    ex =>
                    {
                        logger.LogWarning(ex, "RunTests could not run the Tests area of {Path}", resolvedPath);
                        Answer(Json(new { status = "Error", path = resolvedPath, message = ex.Message }));
                    });
            return Disposable.Empty;
        });
    }

    private string Json(object value) => JsonSerializer.Serialize(value, hub.JsonSerializerOptions);

    // ONE subscription to the Tests area for the whole run: every changed row becomes a log line,
    // and the first frame that is not a progress frame is the verdict.
    private IObservable<Unit> WatchTestsArea(
        string nodePath, AccessContext? caller, IPathResolver pathResolver, TimeSpan budget, ActivityContext ctx) =>
        Observable.Defer(() =>
        {
            // The last frame seen, so a timeout can NAME the case that never finished. Written only
            // from the frame pipeline, which the stream delivers serially.
            TestsAreaFrame? last = null;
            return WatchTestsFrames(nodePath, caller, pathResolver, ctx)
                .Do(frame => last = frame)
                .SkipWhile(frame => frame.Transient)
                .Take(1)
                .Timeout(budget)
                .Catch<TestsAreaFrame, TimeoutException>(_ =>
                {
                    var running = last?.Rows.Where(r => r.Result == "▶").Select(r => r.Case).ToImmutableArray() ?? [];
                    return Observable.Throw<TestsAreaFrame>(new TimeoutException(
                        $"no verdict from the Tests area of {nodePath} within {budget.TotalSeconds:F0}s — "
                        + (running.Length > 0 ? $"still running: {string.Join(", ", running)}" : "no case reported running")));
                })
                .Select(frame =>
                {
                    if (frame.NotFound)
                        throw new InvalidOperationException($"{nodePath} has no Tests area.");
                    var (passed, total) = frame.Counts();
                    ctx.Log(new LogMessage($"{passed} of {total} cases passed",
                            passed == total ? LogLevel.Information : LogLevel.Warning)
                        .WithKey("activity.tests.summary", ("passed", (object?)passed), ("total", (object?)total)));
                    if (!frame.Passed)
                        // The area's own words (its title and, for a suite rendered as markdown, its
                        // failing rows) — upstream text, carried verbatim by the activity's failure line.
                        throw new InvalidOperationException(string.Join(" · ",
                            new[] { frame.Title ?? nodePath + " tests" }
                                .Concat(frame.Rows.Length == 0 ? frame.Text.Where(t => t.Contains('❌')) : [])));
                    return Unit.Default;
                });
        });

    private IObservable<TestsAreaFrame> WatchTestsFrames(
        string nodePath, AccessContext? caller, IPathResolver pathResolver, ActivityContext ctx) =>
        pathResolver.ResolvePath(nodePath)
            .Take(1)
            .SelectMany(resolution => resolution is null
                ? Observable.Throw<TestsAreaFrame>(new InvalidOperationException($"Not found: {nodePath}"))
                : Observable.Defer(() =>
                {
                    var accessService = hub.ServiceProvider.GetService<AccessService>();
                    // Same identity rule as RenderArea: the subscribe must carry the CALLER, or the
                    // owner's read gate would be bypassed under System.
                    using var scope = caller is not null ? accessService?.SwitchAccessContext(caller) : null;
                    var stream = hub.StreamSubscribingHub().GetWorkspace()
                        .GetRemoteStream<JsonElement, LayoutAreaReference>(
                            (Address)resolution.Prefix, new LayoutAreaReference("Tests") { Id = "" });
                    return stream.Select(change => TestsAreaFrame.Read(change.Value))
                        .Finally(stream.Dispose);
                }))
            .Where(frame => frame.Materialized)
            .Scan((Frame: (TestsAreaFrame?)null, Printed: ImmutableDictionary<string, TestsAreaFrame.Row>.Empty),
                (state, frame) =>
                {
                    var (changed, printed) = TestsAreaFrame.Changes(state.Printed, frame);
                    foreach (var row in changed.Where(r => r.Result != "⏳"))
                        ctx.Log(CaseLine(row));
                    return (frame, printed);
                })
            .Select(state => state.Frame!);

    // One case as a keyed activity line: the viewer's language renders the frame, the case's own
    // name and output stay as written.
    private static LogMessage CaseLine(TestsAreaFrame.Row row)
    {
        var level = row.Result.StartsWith('❌') || row.Result.StartsWith('✖') ? LogLevel.Warning : LogLevel.Information;
        if (row.Output.Length == 0)
            return new LogMessage(TestsAreaFrame.Line(row), level)
                .WithKey("activity.tests.case", ("result", (object?)row.Result), ("case", (object?)row.Case), ("time", (object?)row.Time));
        return new LogMessage(TestsAreaFrame.Line(row), level)
            .WithKey("activity.tests.caseOutput", ("result", (object?)row.Result), ("case", (object?)row.Case), ("time", (object?)row.Time), ("output", (object?)row.Output));
    }
}
