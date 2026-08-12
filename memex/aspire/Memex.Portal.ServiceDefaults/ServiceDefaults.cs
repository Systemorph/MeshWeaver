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
        builder.Services.AddHttpClient("plugin-registry")
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler();
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
                        // Don't trace requests to the health endpoint
                        tracing2.Filter = httpContext =>
                            !(httpContext.Request.Path.StartsWithSegments("/health")
                              || httpContext.Request.Path.StartsWithSegments("/alive"))
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
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.UseRequestTimeouts();

        // All health checks must pass for app to be considered ready
        app.MapHealthChecks("/health");

        // Only health checks tagged with "live" must pass for app to be considered alive
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });

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
    /// </summary>
    public static WebApplication MapDrainEndpoint(this WebApplication app)
    {
        app.MapGet("/drain", (IServiceProvider services) =>
        {
            // GetService, not GetRequired: a host without Blazor (a worker, a test host) has no
            // tracker and is trivially drained — never a 500 that a preStop would read as "keep
            // waiting" and then hard-kill at the grace ceiling anyway.
            var tracker = services.GetService<ActiveCircuitTracker>();
            var live = tracker?.Count ?? 0;
            return live == 0
                ? Results.Text("drained", "text/plain")
                : Results.Text($"{live} circuit(s) still open", "text/plain",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        return app;
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
    public static BuildIdentity Build { get; } = ReadBuildIdentity(SelectBuildAssembly());

    /// <summary>Web defaults ⇒ camelCase, so the contract is <c>{"version":…,"commit":…}</c>.</summary>
    private static readonly JsonSerializerOptions VersionJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The assembly that represents this build. In the portal this is the entry assembly — the
    /// executable — which the root <c>Directory.Build.props</c> stamps with both the platform
    /// version and the commit (<c>memex/Directory.Build.props</c> imports the root, so every
    /// portal project inherits the stamps). Reading the entry assembly is what keeps this endpoint
    /// and the About tab reporting the same build.
    ///
    /// <para>When the process was launched by a host that is NOT part of this build, the entry
    /// assembly carries no <c>CommitHash</c> and reading it would report the HOST's version — a
    /// test runner's <c>1.0.0</c> with no commit. (<c>test/Directory.Build.props</c> deliberately
    /// does not import the root props, so this is exactly the shape under test.) The fallback is
    /// this assembly: the same build stamps every one of its assemblies with the same commit, so
    /// the answer is the real one rather than a foreign host's.</para>
    /// </summary>
    internal static Assembly SelectBuildAssembly()
    {
        var entry = Assembly.GetEntryAssembly();
        return entry is not null && CommitOf(entry) is not null ? entry : typeof(ServiceDefaults).Assembly;
    }

    /// <summary>
    /// Projects an assembly's build stamps to the wire record. Mirrors the reflection behind the
    /// About tab (<c>ShippedReleaseSeed.InstalledPlatformVersion</c> / <c>.CommitHash</c>) — the two
    /// readers are separate only because <c>Memex.Portal.Shared</c> sits far above this
    /// infrastructure project and must not be pulled into it; both read the SAME two attributes, so
    /// the page and the endpoint report the same build.
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
