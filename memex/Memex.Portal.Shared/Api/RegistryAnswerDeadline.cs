using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// 🚨 The registry ANSWERS or REFUSES — it never holds a consumer's request open with nothing
/// written (MeshWeaver#4963).
///
/// <para><b>What it closes.</b> The registry routes (<c>/api/plugins</c>, <c>/api/plugins/bundles/*</c>)
/// bounded each of their live reads separately — the instance-key legs at 10 s, the query fan-in at
/// 15 s — and nothing bounded the REQUEST. A stage with no bound of its own (the served activation
/// list's shared derivation over the module records on the share, the catalog's shared source
/// listing, a host-supplied seam) could therefore hold the request for as long as it held, with no
/// status line written and no line logged. Measured on 2026-10-07: consumers in two namespaces got
/// <c>0 bytes</c> for 120 s from <c>memex.meshweaver.cloud</c> (memex-cloud's own three replicas
/// 13:53–13:57Z, the control instance 08:35–09:12Z) while the registry's replicas logged no line for
/// any of those requests and their liveness heartbeats ticked every 10 s on time.</para>
///
/// <para><b>What it does.</b> Every request carries a <see cref="RegistryRequestStages"/> ledger,
/// started at the auth filter (the request's arrival at the route). Each stage that can wait —
/// authentication, each mesh read, the activation list, the source listing — enters it while it
/// runs. If the route has produced no answer within its budget after arrival, it answers
/// <b>503 + <c>Retry-After</c></b> and logs ONE warning naming the stage(s) still waiting and how
/// long each finished one took. The abandoned stage is not cancelled for anyone else: the shared
/// reads behind it are service-owned and keep running for the next request.</para>
///
/// <para><b>Why this is the discriminator the incident lacked.</b> From now on a consumer-side
/// <c>NoResponse</c> with NO registry-side "produced no answer" line means the request never reached
/// the route (ingress, network, the consumer's own connection) — and a line names the stage that held
/// it. Before, both readings looked identical: silence on both sides.</para>
///
/// <para><b>The budget is a property of the CONSUMERS, not a dial.</b> A route answers before the
/// shortest client attempt that reads it gives up, so a stalled route reaches that client as a
/// retryable refusal its resilience pipeline honours (<c>Retry-After</c>) instead of a cut with no
/// cause; and later than the slowest HEALTHY answer measured, so a slow-but-working route is never
/// turned into a refusal. Index and catalog: <see cref="AnswerBudget"/> — under
/// <c>plugin-registry-standard</c>'s 30 s attempt, above the 19.4 s slowest healthy index
/// (memex-cloud, 2026-10-07 13:57:35Z). Bundle download: <see cref="DownloadBudget"/> — a healthy
/// download takes ~15–16 s on this registry because it re-derives the activation list twice (a
/// 1,156-byte bundle took 15,318 ms, memex, 2026-10-07 13:58:54Z), and the bundle client's own
/// budget is 120 s.</para>
/// </summary>
internal static class RegistryAnswerDeadline
{
    /// <summary>Configuration key overriding every route's budget, in seconds (a test's short clock).</summary>
    public const string BudgetSecondsConfigKey = "PluginCatalog:RegistryAnswerBudgetSeconds";

    /// <summary>The index's, the catalog's and the package-files route's budget: 25 s, between 19.4 s
    /// and 30 s (class remarks). <c>POST /api/plugins/files</c> is read by the same
    /// <c>plugin-registry-standard</c> client, with the same 30 s attempt (MeshWeaver#5825).</summary>
    public static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(25);

    /// <summary>A bundle download's budget: 60 s, between ~16 s and 120 s (class remarks).</summary>
    public static readonly TimeSpan DownloadBudget = TimeSpan.FromSeconds(60);

    private const string StagesItemKey = "PluginRegistry.AnswerStages";

    /// <summary>The request's stage ledger, started on first use — the auth filter's, so its clock
    /// is the request's arrival at the route.</summary>
    public static RegistryRequestStages Stages(HttpContext http)
    {
        if (http.Items.TryGetValue(StagesItemKey, out var existing) && existing is RegistryRequestStages stages)
            return stages;
        var started = new RegistryRequestStages();
        http.Items[StagesItemKey] = started;
        return started;
    }

    /// <summary>The configured override, or the route family's own <paramref name="budget"/>.</summary>
    public static TimeSpan Budget(HttpContext http, TimeSpan budget) =>
        double.TryParse(http.RequestServices.GetService<IConfiguration>()?[BudgetSecondsConfigKey],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : budget;

    /// <summary>Runs <paramref name="source"/> as the named stage of the request: entered on
    /// subscribe, finished on its first value, its completion or its fault — whichever comes first.
    /// 🚨 NOT on unsubscribe: the deadline unsubscribes the stalled stage before it describes the
    /// request, and a stage abandoned while it waited is exactly the stage it must name.</summary>
    public static IObservable<T> InStage<T>(this IObservable<T> source, RegistryRequestStages? stages, string name) =>
        stages is null ? source : Observable.Defer(() =>
        {
            var stage = stages.Enter(name);
            return source.Do(_ => stage.Finish(), _ => stage.Finish(), stage.Finish);
        });

    /// <summary>
    /// The route's answer, or — when it has produced none <paramref name="budget"/> after the
    /// request arrived — 503 + <c>Retry-After</c> and one warning naming what it was waiting on.
    /// </summary>
    /// <param name="answer">The route's answer.</param>
    /// <param name="http">The request: the refusal's <c>Retry-After</c> goes on its response.</param>
    /// <param name="budget">How long after arrival the route may take to answer.</param>
    /// <param name="retryAfterSeconds">The route family's own <c>Retry-After</c> value.</param>
    /// <param name="logger">Resolved while the request scope was alive.</param>
    /// <param name="scheduler">The clock — virtual in tests, the default scheduler otherwise.</param>
    public static IObservable<IResult> AnsweredWithin(
        this IObservable<IResult> answer, HttpContext http, TimeSpan budget, int retryAfterSeconds,
        ILogger? logger, IScheduler? scheduler = null)
    {
        var stages = Stages(http);
        var clock = scheduler ?? DefaultScheduler.Instance;
        return Observable.Defer(() =>
        {
            // 🚨 Everything the refusal needs from the REQUEST is read HERE, at subscribe — while the
            // request is alive — and never inside the timeout. The timeout fires on a timer thread,
            // possibly after the client has hung up and the host has disposed the request's feature
            // collection; reading `http.Request.Method` there threw `ObjectDisposedException:
            // IFeatureCollection has been disposed` (fleet registry, 2026-10-09 20:03:29Z/20:03:32Z),
            // the late-fault shape of #5999 in the deadline's own write path.
            var request = new RefusedRequest(http.Request.Method, http.Request.Path.ToString(), http.RequestAborted);
            var remaining = budget - stages.Elapsed;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return answer.Take(1).Timeout(remaining,
                Observable.Defer(() => Observable.Return(Unanswered(request, stages, budget, retryAfterSeconds, logger))),
                clock);
        });
    }

    private static IResult Unanswered(
        RefusedRequest request, RegistryRequestStages stages, TimeSpan budget, int retryAfterSeconds, ILogger? logger)
    {
        var account = stages.Describe();
        if (request.Aborted.IsCancellationRequested)
            // The client is already gone: nothing will be written, so this is not a refusal anybody
            // receives — but the stage account is still the reading the incident needs.
            logger?.LogInformation(
                "Plugin registry: {Method} {Path} produced no answer within {Budget:F1} s of arriving, and the "
                + "client had already disconnected — nothing is written. {Account}",
                request.Method, request.Path, budget.TotalSeconds, account);
        else
            logger?.LogWarning(
                "Plugin registry: {Method} {Path} produced no answer within {Budget:F1} s of arriving — answering "
                + "503 + Retry-After instead of holding the connection open with nothing written. {Account}",
                request.Method, request.Path, budget.TotalSeconds, account);
        // The Retry-After header is written when the result EXECUTES, against the context the host
        // hands it — so a refusal whose client has gone never touches a disposed response.
        return new RetryAfterResult(
            Results.Json(
                new
                {
                    error = $"The registry could not answer within {budget.TotalSeconds:F1} s — retry shortly. "
                        + "This says nothing about your key, your grant or the package.",
                    waitingOn = stages.Pending(),
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            retryAfterSeconds);
    }

    /// <summary>What the refusal needs from the request, captured while the request was alive.</summary>
    private sealed record RefusedRequest(string Method, string Path, CancellationToken Aborted);
}

/// <summary>
/// A refusal that carries its <c>Retry-After</c> to the moment it is written: the header goes on the
/// response the host executes it against, never on a context captured earlier (#4963).
/// </summary>
internal sealed class RetryAfterResult(IResult inner, int retryAfterSeconds) : IResult, IStatusCodeHttpResult
{
    /// <summary>The seconds the client is told to wait before asking again.</summary>
    public int RetryAfterSeconds { get; } = retryAfterSeconds;

    /// <inheritdoc />
    public int? StatusCode => (inner as IStatusCodeHttpResult)?.StatusCode;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return inner.ExecuteAsync(httpContext);
    }
}

/// <summary>
/// One request's stages, in the order they were entered: which have finished and in how long, and
/// which are still running. Lock-free (stages finish on whatever thread delivered their frame);
/// per request, never shared.
/// </summary>
internal sealed class RegistryRequestStages
{
    private readonly long arrivedAt = Stopwatch.GetTimestamp();
    private ImmutableList<Stage> stages = ImmutableList<Stage>.Empty;

    /// <summary>Time since the request arrived at the route.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(arrivedAt);

    /// <summary>Enters a stage; the returned handle finishes it (idempotently).</summary>
    public Stage Enter(string name)
    {
        var stage = new Stage(name, Stopwatch.GetTimestamp());
        ImmutableInterlocked.Update(ref stages, list => list.Add(stage));
        return stage;
    }

    /// <summary>The names of the stages still running, in the order they were entered.</summary>
    public IReadOnlyList<string> Pending() =>
        Volatile.Read(ref stages).Where(s => !s.IsFinished).Select(s => s.Name).Distinct().ToArray();

    /// <summary>One sentence: what the route was waiting on, and what it had finished.</summary>
    public string Describe()
    {
        var all = Volatile.Read(ref stages);
        if (all.IsEmpty)
            return "No stage had started — the request was held before the route read anything.";
        var pending = all.Where(s => !s.IsFinished)
            .Select(s => $"{s.Name} (running {s.Duration.TotalSeconds:F1} s)").ToArray();
        var done = all.Where(s => s.IsFinished)
            .Select(s => $"{s.Name} {s.Duration.TotalSeconds:F1} s").ToArray();
        return (pending.Length > 0
                   ? $"Still waiting on: {string.Join("; ", pending)}."
                   : "Every stage had finished — the answer was being composed.")
               + (done.Length > 0 ? $" Finished: {string.Join("; ", done)}." : " Nothing had finished.");
    }

    /// <summary>One entered stage.</summary>
    internal sealed class Stage(string name, long startedAt)
    {
        private long finishedAt;

        /// <summary>The stage's name.</summary>
        public string Name { get; } = name;

        /// <summary>Whether it finished.</summary>
        public bool IsFinished => Interlocked.Read(ref finishedAt) != 0;

        /// <summary>How long it ran, or has been running.</summary>
        public TimeSpan Duration =>
            Interlocked.Read(ref finishedAt) is var end && end != 0
                ? Stopwatch.GetElapsedTime(startedAt, end)
                : Stopwatch.GetElapsedTime(startedAt);

        /// <summary>Finishes the stage; a second call keeps the first time.</summary>
        public void Finish() => Interlocked.CompareExchange(ref finishedAt, Stopwatch.GetTimestamp(), 0);
    }
}
