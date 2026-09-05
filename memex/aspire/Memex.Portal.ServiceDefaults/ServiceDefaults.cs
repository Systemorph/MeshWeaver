using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// Common .NET Aspire services for Memex portal: service discovery, resilience, health checks, and OpenTelemetry.
/// </summary>
public static class ServiceDefaults
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();
            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Attributable resilience for the plugin-registry client (#1133/#1137). Every client
        // otherwise shares the ONE unnamed defaults pipeline above, whose Polly events log
        // Source: '-standard//…' with an empty operation key — the boot-time registry timeouts
        // could not be attributed to any call path. Re-registering the named client the
        // PluginCatalog consumer resolves (InstanceRegistrationClient.HttpClientName — the
        // literal is duplicated here because ServiceDefaults deliberately does not reference
        // MeshWeaver.PluginCatalog) swaps the shared default pipeline for its own standard one,
        // so the same event now reads 'plugin-registry-standard//…'. Same policies, named.
        // RemoveAllResilienceHandlers is [Experimental] (EXTEXP0001) — the pragma is the API's
        // designed opt-in, and it is the ONLY way to override the defaults pipeline per client
        // without stacking a second retry-inside-retry pipeline on top of it.
#pragma warning disable EXTEXP0001
        // 🚨 TWO clients, because the registry serves two call shapes with OPPOSITE budgets.
        //
        // What the standard defaults (10s per attempt, 30s total) cost us, measured 2026-08-26 from
        // inside two production portals: GET /api/plugins/bundles/index.json — an 8.7 KB document —
        // connects in ~0.03s and then takes 12–19s to first byte, because the registry evaluates
        // entitlement per package and runs a mesh query on every request. TTFB alone exceeds the
        // 10s ATTEMPT timeout, so every attempt is cancelled, all three retries are cancelled the
        // same way, and the pipeline reports TotalRequestTimeout at 30s.
        //
        // In production that read as `Module 'MeshWeaver.SelfUpdate.Aks' of Hosting: landing
        // failed`, repeatedly, on memex.systemorph.com — three consequences deep, none naming a
        // timeout:
        //   1. modules stopped landing — 40 present against a sibling's 55;
        //   2. the ones that did land pre-dated the asset fix, so `_content/…` 404'd and pages
        //      died with "Importing a module script failed";
        //   3. MeshWeaver.SelfUpdate.Aks never landed, so the real Kubernetes patcher was never
        //      registered, IDeploymentUpdater stayed DetectOnly, and the instance recorded an
        //      available version forever while patching nothing. Self-update was silently off.
        //
        // Raising the budget is a MITIGATION, not the cure — a registry this slow to serve 8.7 KB
        // is its own defect and wants caching — but a client whose budget cannot cover the server's
        // OBSERVED latency keeps failing after the server improves, and it fails invisibly.
        //
        // 🚨 The raise must NOT be shared, though, and that is the half worth reading twice. Bundle
        // transfers are megabytes off that slow index and legitimately want minutes. Registration
        // and the /Store catalog listing are rendered on a PAGE, where minutes are not resilience —
        // they are a hang. One pipeline for both would have traded a silent module failure for a
        // /Store that spins for five minutes against an unreachable registry.
        //
        // Polly validates each set against itself: TotalRequestTimeout must exceed AttemptTimeout,
        // and the breaker's SamplingDuration must be at least twice AttemptTimeout — so the three
        // values move together or the handler throws at startup.

        // Both names are STRING LITERALS on purpose: this assembly does not reference
        // MeshWeaver.PluginCatalog and should not start to for two constants. They must match
        // InstanceRegistrationClient.HttpClientName / .BundleHttpClientName, whose doc comments say
        // the same thing from the other side. A name that drifts does not fail — the factory simply
        // hands back an unconfigured client and the budget below applies to nothing.

        // Page-facing: registration + the catalog listing. Generous enough to cover the measured
        // 12–19s TTFB with headroom, bounded tightly enough that a user is never left waiting.
        builder.Services.AddHttpClient("plugin-registry")
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        // Transfer-facing: bundle downloads only. Nothing renders behind this, so it may wait.
        builder.Services.AddHttpClient("plugin-registry-bundles")
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
            });
#pragma warning restore EXTEXP0001

        builder.Services.AddRequestTimeouts();
        builder.Services.AddOutputCache();

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter("Microsoft.Orleans")
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource("Microsoft.Orleans.Runtime");
                tracing.AddSource("Microsoft.Orleans.Application");
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing2 =>
                        // Don't trace requests to the probe endpoints — a kubelet polls all three
                        // every few seconds for the life of the pod, and none of it is a trace
                        // anybody reads.
                        tracing2.Filter = httpContext =>
                            !(httpContext.Request.Path.StartsWithSegments(ProbeEndpoints.Health)
                              || httpContext.Request.Path.StartsWithSegments(ProbeEndpoints.Live)
                              || httpContext.Request.Path.StartsWithSegments(ProbeEndpoints.Ready))
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static void AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        // Observability ships to the Prometheus / Grafana / Loki (LGTM) stack via OTLP.
        // Metrics/traces export to the OTel Collector when OTEL_EXPORTER_OTLP_ENDPOINT is
        // configured (local Colima k3s + AKS both set it). Logs reach Loki out-of-band via
        // Promtail scraping pod stdout — no app wiring needed here.
        var useOtlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlp)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddRequestTimeouts(
            configure: static timeouts =>
                timeouts.AddPolicy("HealthChecks", TimeSpan.FromSeconds(20)));

        builder.Services.AddOutputCache(
            configureOptions: static caching =>
                caching.AddPolicy("HealthChecks",
                    build: static policy => policy.Expire(TimeSpan.FromSeconds(20))));

        builder.Services.AddHealthChecks()
            // The trivial process-up check: the process is running and can execute a delegate.
            //
            // 🚨 It carries BOTH probe tags, and that is what keeps ProbeEndpoints.Ready
            // non-vacuous. A MapHealthChecks whose predicate matches NOTHING answers 200 for any
            // process that can still accept a socket — the exact blindness that let two replicas
            // serve hung pages for three and a half hours on 2026-08-25 while /alive reported
            // Healthy with an empty "live" set (MeshWeaver#2194). A readiness endpoint that could
            // not fail would be that same defect, rebuilt.
            .AddCheck("self", () => HealthCheckResult.Healthy(),
                [ProbeEndpoints.LiveTag, ProbeEndpoints.ReadyTag]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.UseRequestTimeouts();

        // All health checks must pass for app to be considered ready
        app.MapHealthChecks(ProbeEndpoints.Health);

        // Only health checks tagged with "live" must pass for app to be considered alive
        app.MapHealthChecks(ProbeEndpoints.Live,
            new HealthCheckOptions { Predicate = r => r.Tags.Contains(ProbeEndpoints.LiveTag) });

        // Only health checks tagged with "ready" decide whether this pod stays in the Service.
        app.MapHealthChecks(ProbeEndpoints.Ready,
            new HealthCheckOptions { Predicate = r => r.Tags.Contains(ProbeEndpoints.ReadyTag) });

        app.MapVersionEndpoint();
        app.MapDrainEndpoint();

        return app;
    }

    /// <summary>
    /// <c>/drain</c> — "may this pod stop without cutting anyone off?" Answers <b>200 drained</b>
    /// when no Blazor circuit is live here, <b>503</b> with the count while sessions remain.
    ///
    /// <para>The container's <c>preStop</c> polls it, so a rollout stops SIGTERMing a pod that is
    /// still serving people. Until now preStop was a flat <c>sleep 15</c> — enough to drain the
    /// ingress upstream, nothing more — and fifteen seconds after the replacement went ready every
    /// circuit on the old pod died mid-sentence, along with the grains they had activated
    /// (<c>MessageHubGrain</c> is <c>[PreferLocalPlacement]</c>, so a circuit's hubs live on the pod
    /// serving it). With a 6-hourly self-update poller, that arrived unannounced.</para>
    ///
    /// <para>It reports, it does not decide: the ceiling stays
    /// <c>terminationGracePeriodSeconds</c>, so a pod with a forgotten open tab cannot block a
    /// rollout forever. No session, no state, no auth — infrastructure, exactly like
    /// <c>/health</c> and <c>/alive</c> beside it.</para>
    ///
    /// <para><b>…and it LOGS what it reports.</b> preStop probes with
    /// <c>curl -sf -m 5 -o /dev/null</c>, which throws the count away and cannot tell a 503 from a
    /// refused connection — so without a log line a pod sitting in <c>Terminating</c> is opaque
    /// from outside, and "one forgotten tab" is indistinguishable from "the HTTP layer is wedged"
    /// (#1794). Since a probe of this endpoint is the only notice the process gets that it is
    /// terminating at all — preStop runs BEFORE SIGTERM — the probe is also the right moment to say
    /// so. <see cref="DrainProgress"/> holds the rate limiting and the decision; see its remarks
    /// for how the three cases read in the log.</para>
    /// </summary>
    public static WebApplication MapDrainEndpoint(this WebApplication app)
    {
        // Process-wide, captured here rather than resolved: the endpoint is mapped once per app, so
        // the closure IS the process scope, and this stays independent of whether the host happens
        // to register Blazor's tracker. See DrainProgress for why the endpoint — not a timer — is
        // the right place to notice that this pod is terminating.
        var progress = new DrainProgress();

        app.MapGet("/drain", (IServiceProvider services, ILogger<DrainProgress> logger) =>
        {
            // GetService, not GetRequired: a host without Blazor (a worker, a test host) has no
            // tracker and is trivially drained — never a 500 that a preStop would read as "keep
            // waiting" and then hard-kill at the grace ceiling anyway.
            var tracker = services.GetService<ActiveCircuitTracker>();
            var live = tracker?.Count ?? 0;

            Report(logger, progress.Probe(live, DateTimeOffset.UtcNow));

            return live == 0
                ? Results.Text("drained", "text/plain")
                : Results.Text($"{live} circuit(s) still open", "text/plain",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        // 🚨 What SIGTERM FOUND — and the line whose ABSENCE is the evidence of a hard kill
        // (#1971). preStop used to poll /drain with no bound of its own, so a pod whose sessions
        // outlived terminationGracePeriodSeconds was SIGKILLed with a live Orleans silo: this
        // callback never ran, the host's 90 s ShutdownTimeout never ran, and the silo never
        // departed membership. The deployment's safe-to-evict annotation records what that costs
        // ("each abrupt departure left a ZOMBIE entry in the Orleans membership table … writes
        // timed out mesh-wide"). With preStop bounded to drainSeconds − shutdownMarginSeconds,
        // SIGTERM arrives INSIDE the grace and this runs — so "did the pod depart cleanly" becomes
        // a question Loki can answer, which it could not before in either direction.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger<DrainProgress>();
            if (logger is null)
                return;

            var tracker = app.Services.GetService<ActiveCircuitTracker>();
            var report = progress.Abandon(tracker?.Count ?? 0, DateTimeOffset.UtcNow);

            if (!report.TerminationWasObserved)
                logger.LogInformation(
                    "Drain: SHUTDOWN with no drain probe ever seen — SIGTERM arrived without a "
                    + "preStop (a node eviction, a local Ctrl-C, or a chart that lost its "
                    + "lifecycle hook). {Live} circuit(s) were open. Shutting down in order.",
                    report.LiveCircuits);
            else if (report.CutSessionsOff)
                logger.LogWarning(
                    "Drain: GIVING UP after {Elapsed} — {Live} circuit(s) are STILL OPEN and are "
                    + "being cut off ({Initial} were open when termination began). The drain "
                    + "window expired, so preStop returned and SIGTERM was delivered while there "
                    + "is still grace left to shut down in. Cutting off the last stragglers "
                    + "deliberately is the trade: riding to the ceiling instead would SIGKILL this "
                    + "process with a live silo, leaving a zombie membership entry the cluster "
                    + "keeps placing activations on. Raise portal.drainSeconds if sessions this "
                    + "long are expected.",
                    report.Elapsed, report.LiveCircuits, report.CircuitsWhenTerminationBegan);
            else
                logger.LogInformation(
                    "Drain: shutting down cleanly after {Elapsed} — no circuits remain "
                    + "({Initial} were open when termination began). The silo departs membership "
                    + "in order.",
                    report.Elapsed, report.CircuitsWhenTerminationBegan);
        });

        return app;
    }

    /// <summary>
    /// Writes the one line a probe is worth. Information: a pod termination is a rare lifecycle
    /// event, and <see cref="DrainProgress.ReportInterval"/> already caps a full 1800 s drain at
    /// roughly thirty lines. Each line is self-sufficient — it states the counts and the elapsed
    /// time rather than requiring the reader to diff it against an earlier one.
    /// </summary>
    private static void Report(ILogger logger, DrainProbeReport report)
    {
        switch (report.Outcome)
        {
            case DrainProbeOutcome.TerminationBegun:
                logger.LogInformation(
                    "Drain: TERMINATION BEGUN — preStop is polling /drain, so Kubernetes has already " +
                    "deleted this pod and removed it from the Service; the process has NOT been " +
                    "SIGTERMed yet and keeps serving its {Live} open circuit(s) until they close or " +
                    "the grace ceiling SIGKILLs it. Treat every log line after this one as coming " +
                    "from a terminating replica, not a serving one.",
                    report.LiveCircuits);
                break;

            case DrainProbeOutcome.StillDraining:
                logger.LogInformation(
                    "Drain: still draining after {Elapsed} — {Live} circuit(s) open (was " +
                    "{Previous} at the last report, {Initial} when termination began) over " +
                    "{Probes} probe(s). {Verdict}",
                    report.Elapsed,
                    report.LiveCircuits,
                    report.CircuitsAtLastReport,
                    report.CircuitsWhenTerminationBegan,
                    report.ProbeCount,
                    report.Progressing
                        ? "Progressing — sessions are closing."
                        : "NO progress since the last report; if the count stays flat this pod " +
                          "will ride the grace period to SIGKILL.");
                break;

            case DrainProbeOutcome.Drained:
                logger.LogInformation(
                    "Drain: DRAINED after {Elapsed} over {Probes} probe(s) — the last circuit " +
                    "closed ({Initial} were open when termination began). preStop returns now and " +
                    "the process shuts down normally.",
                    report.Elapsed,
                    report.ProbeCount,
                    report.CircuitsWhenTerminationBegan);
                break;

            case DrainProbeOutcome.Silent:
            default:
                break;
        }
    }

    /// <summary>
    /// The route the build identity is served on. Sits beside <c>/health</c> and <c>/alive</c>
    /// deliberately: like them it is infrastructure, answers without a session, and reports only
    /// what the process IS — never what it holds.
    /// </summary>
    public const string VersionRoute = "/api/version";

    /// <summary>
    /// Maps <c>GET /api/version</c> → <c>{"version":"…","commit":"…"}</c>, ANONYMOUS.
    ///
    /// <para><b>Why unauthenticated.</b> "Is this deployment current?" is the question issue #956
    /// asks, and the Settings → About tab now answers it — but only to someone who can sign in.
    /// Verifying a roll-out from outside (a monitor, a deploy check, a user comparing against
    /// GitHub) needs an answer without a session. Nothing is disclosed by it: MeshWeaver is a
    /// PUBLIC repository, so the version and the commit SHA are already readable on GitHub. This
    /// endpoint says which of those public commits is running, and nothing else.</para>
    ///
    /// <para><b>The response is exactly two fields</b> — no environment name, no cluster or
    /// namespace, no configuration, no partition names, no user data. Serialized with explicit
    /// local options so the wire contract cannot drift if the host later configures global JSON
    /// settings.</para>
    ///
    /// <para>Reads only assembly metadata, so it is answerable even when storage or the mesh is
    /// unhealthy — it touches no hub, no node and no database. Registered separately from
    /// <c>/health</c> and <c>/alive</c>, whose shapes are the Kubernetes probe contract and are
    /// deliberately left untouched.</para>
    /// </summary>
    public static IEndpointRouteBuilder MapVersionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(VersionRoute, () => Results.Json(Build, VersionJsonOptions))
            .AllowAnonymous()
            .WithName("BuildVersion");
        return endpoints;
    }

    /// <summary>
    /// The build identity of THIS process, resolved once at startup — the values are compile-time
    /// constants baked into the assembly, so re-reading them per request would buy nothing.
    /// Immutable and never written after initialization (a constant, not a cache).
    /// </summary>
    public static BuildIdentity Build { get; } = ReadBuildIdentity(PlatformBuildInfo.BuildAssembly);

    /// <summary>Web defaults ⇒ camelCase, so the contract is <c>{"version":…,"commit":…}</c>.</summary>
    private static readonly JsonSerializerOptions VersionJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Projects an assembly's build stamps to the wire record. The About tab
    /// (<c>ShippedReleaseSeed.InstalledPlatformVersion</c> / <c>.CommitHash</c>) reads the same two
    /// attributes off the same assembly — the two readers are separate only because
    /// <c>Memex.Portal.Shared</c> sits far above this infrastructure project and must not be pulled
    /// into it, so the SELECTION of that assembly lives below both, in
    /// <see cref="PlatformBuildInfo.SelectBuildAssembly"/>.
    ///
    /// <para>🚨 It used to be duplicated here instead, and only here — the About tab read
    /// <c>GetEntryAssembly()</c> raw. When the portal executable moved to a repo that stamps no
    /// version (2026-08-25) the copies diverged in production: this endpoint answered
    /// <c>3.0.0-rc9+0a1eabdc…</c> and the page said <c>1.0.0</c>. One selection, one answer.</para>
    /// </summary>
    public static BuildIdentity ReadBuildIdentity(Assembly assembly) => new(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown",
        CommitOf(assembly) ?? "");

    /// <summary>
    /// The git SHA baked in as <c>AssemblyMetadata("CommitHash")</c> by the
    /// <c>AddCommitHashMetadata</c> target, or null when the build carried no source-control
    /// information (a git-less source drop).
    /// </summary>
    private static string? CommitOf(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "CommitHash", StringComparison.OrdinalIgnoreCase))
            ?.Value is { Length: > 0 } sha
            ? sha
            : null;
}

/// <summary>
/// The <c>/api/version</c> response — the whole of it. Which build is running, and which public
/// commit it was produced from; deliberately nothing else.
/// </summary>
/// <param name="Version">The platform version (<c>3.0.0-rc1.ci.{run}</c> for CI builds).</param>
/// <param name="Commit">The full git SHA the build was produced from, or empty when the build
/// carried no source-control information.</param>
public record BuildIdentity(string Version, string Commit);

/// <summary>
/// Distributed cluster configuration constants for Memex.
/// </summary>
public static class MemexDistributedConstants
{
    public const string ServiceId = "Memex";
    public const string ClusterId = "Memex";
}
