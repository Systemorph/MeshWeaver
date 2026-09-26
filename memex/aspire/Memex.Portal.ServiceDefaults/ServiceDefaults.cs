using System.Collections.Immutable;
using System.Globalization;
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
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Persistence;
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

        // 🚨 The instrument that says WHICH KIND of silence a quiet window in this portal's log was
        // (#4234). Every other stall detector here — Orleans' watchdog, the disposal stall verdict,
        // the pending-callback report — fires on an event and reports ON RESUME, so a stop that
        // outlives the process prints nothing, which is precisely the window a wedge investigation
        // reads. One unconditional line per Diagnostics:LivenessHeartbeatSeconds (10 s; 0 = off),
        // on a thread of its own so pool starvation cannot silence it. Armed HERE, in the host
        // whose log is actually kept, and not in MeshHostApplicationBuilder — which would start a
        // thread inside every mesh a test run builds.
        builder.Services.AddProcessLivenessHeartbeat();

        builder.Services.AddServiceDiscovery();

        // 🚨 Resilience for every outbound HttpClient — split out so a test builds exactly what
        // production builds (EveryClientNamesItsOwnResiliencePipelineTest).
        builder.Services.AddHttpClientResilienceDefaults();

        // Turn on service discovery by default. Registered AFTER the resilience defaults so the
        // handler order is the one it always was: resilience outermost, discovery inside it.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        builder.Services.AddRequestTimeouts();
        builder.Services.AddOutputCache();

        return builder;
    }

    /// <summary>
    /// The resilience every outbound <see cref="HttpClient"/> of the portal gets: the standard
    /// handler on the shared defaults (with the #4613 name-does-not-resolve carve-out), ONE
    /// pipeline instance per client NAME, and the two plugin-registry clients re-registered with
    /// their own budgets.
    ///
    /// <para>🚨 <b>What <c>Source: '-standard//…'</c> actually is (#4528).</b> The builder that
    /// <c>ConfigureHttpClientDefaults</c> hands out has NO name, and
    /// <c>AddStandardResilienceHandler</c> names its pipeline <c>{builder.Name}-standard</c> — so
    /// the defaults pipeline is called <c>-standard</c> for EVERY client that does not re-register
    /// itself, named or not. It was read as "some caller resolves an unnamed client"; measured on
    /// memex-cloud it was the NAMED <c>self-update-handover</c> client (every stack under those
    /// timeouts ends in <c>SelfUpdateHandover.Post</c>). And because the pipeline registry keys a
    /// pipeline by (name, instance) and the instance was always empty, it was ONE pipeline — one
    /// circuit breaker — shared by every such client and every host they call.</para>
    ///
    /// <para>The instance is now the client's own name: an outermost handler stamps
    /// <see cref="HttpMessageHandlerBuilder.Name"/> onto the request and <see cref="ClientNameOf"/>
    /// selects the pipeline instance from it. Every Polly line then reads
    /// <c>-standard/self-update-handover/Standard-AttemptTimeout</c>, and each client trips its own
    /// breaker. Keyed by client NAME, never by request authority: names are fixed in code, so the
    /// registry holds a bounded set, whereas the defaults also serve arbitrary URLs taken from user
    /// data (web fetches, Open Graph previews) and a per-host key would grow without bound.</para>
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddHttpClientResilienceDefaults(this IServiceCollection services)
    {
        // The stamp goes on EVERY client, re-registered ones included (removing a client's
        // resilience handlers leaves it in place, harmlessly). Insert(0) makes it the outermost
        // handler whatever order the builder actions run in, so the name is on the request before
        // any pipeline is selected.
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(handlers =>
                handlers.AdditionalHandlers.Insert(0, new HttpClientNameStamp(handlers.Name))));

        services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler(options =>
            {
                // 🚨 A HOSTNAME THAT DOES NOT EXIST IS NOT A TRANSIENT FAULT (#4613). The standard
                // predicate handles every HttpRequestException, so an NXDOMAIN was retried three
                // times — and no retry can make a name resolve. What that cost, measured on the
                // control instance 2026-09-17: two agent web fetches of `www.boss-software.ch` and
                // `www.bosssw.ch` — hostnames that do not exist ANYWHERE (all four spellings,
                // including both apex domains, answer NXDOMAIN from the public internet, so this is
                // not cluster DNS, not egress and not a missing route) — spent three attempts each
                // and logged every one at Error under category `Polly`. The fleet's log watcher
                // folds Error lines into a LogIncident and opens a ticket, so a URL in somebody's
                // data manufactured a platform defect report.
                //
                // Excluded from the BREAKER for the same reason and one more: one client's pipeline
                // (one per client name since #4528, one for ALL of them before it) serves every host
                // that client calls — the web fetcher calls whatever URL an agent was given — so
                // counting a dead hostname as a failure lets one bad URL push the breaker toward
                // open for calls that have nothing to do with it. A name that does not resolve says
                // nothing about the health of any endpoint.
                //
                // 🚨 Narrowest possible set: HostNotFound only. `TryAgain` (EAI_AGAIN) is a DNS
                // server that did not answer — genuinely transient, and it must keep being retried.
                // Every other transport failure (TLS, connection reset, timeout) is untouched.
                var transient = options.Retry.ShouldHandle;
                options.Retry.ShouldHandle = args => NameDoesNotResolve(args.Outcome.Exception)
                    ? ValueTask.FromResult(false)
                    : transient(args);
                var breaks = options.CircuitBreaker.ShouldHandle;
                options.CircuitBreaker.ShouldHandle = args => NameDoesNotResolve(args.Outcome.Exception)
                    ? ValueTask.FromResult(false)
                    : breaks(args);
            })
            // 🚨 ONE PIPELINE INSTANCE PER CLIENT NAME (#4528) — see ClientNameOf.
            .SelectPipelineBy(static _ => ClientNameOf);
        });

        // Attributable resilience for the plugin-registry client (#1133/#1137). Every other client
        // rides the defaults pipeline above (Source: '-standard/{client}/…' since #4528; before it,
        // '-standard//…' with an empty instance, which is why the boot-time registry timeouts
        // could not be attributed to any call path). The registry clients need more than a name,
        // though: a budget of their own. Re-registering the named client the
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
        services.AddHttpClient("plugin-registry")
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        // Transfer-facing: bundle downloads only. Nothing renders behind this, so it may wait.
        services.AddHttpClient("plugin-registry-bundles")
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
            });
#pragma warning restore EXTEXP0001

        return services;
    }

    /// <summary>The request option the outermost handler writes the sending client's name into.</summary>
    internal static readonly HttpRequestOptionsKey<string> ClientNameKey = new("MeshWeaver.HttpClientName");

    /// <summary>The pipeline instance of a request sent by the factory's default, UNNAMED client.
    /// Spelled out so the log line says so rather than leaving the slot empty — an empty slot is
    /// exactly what hid the caller.</summary>
    internal const string UnnamedClient = "(unnamed)";

    /// <summary>
    /// The pipeline instance a request belongs to: the name of the client that sent it, as the
    /// outermost handler stamped it; <see cref="UnnamedClient"/> for the default client or for a
    /// request that did not come through a stamped chain. Pure.
    /// </summary>
    /// <param name="request">The outgoing request.</param>
    /// <returns>The pipeline instance name.</returns>
    internal static string ClientNameOf(HttpRequestMessage request) =>
        request.Options.TryGetValue(ClientNameKey, out var name) && !string.IsNullOrEmpty(name)
            ? name
            : UnnamedClient;

    /// <summary>
    /// Writes the owning client's name onto every request it sends, so the resilience pipeline
    /// inside it is selected per client (<see cref="ClientNameOf"/>). The HTTP stack's own Task
    /// boundary — nothing is awaited here.
    /// </summary>
    private sealed class HttpClientNameStamp(string? clientName) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Stamp(request);
            return base.SendAsync(request, cancellationToken);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Stamp(request);
            return base.Send(request, cancellationToken);
        }

        private void Stamp(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(clientName))
                request.Options.Set(ClientNameKey, clientName);
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is a request that failed because the HOSTNAME DOES NOT
    /// EXIST — the one transport failure that is a fact about the URL rather than about the network
    /// or the remote service, and therefore the one a retry can never fix (#4613).
    ///
    /// <para>Walks the inner chain: <c>HttpClient</c> wraps the resolver's
    /// <see cref="System.Net.Sockets.SocketException"/> in an
    /// <c>HttpRequestException</c>, and a handler pipeline can wrap that again.</para>
    ///
    /// <para>🚨 <see cref="System.Net.Sockets.SocketError.HostNotFound"/> ONLY. <c>TryAgain</c>
    /// (EAI_AGAIN) is a DNS server that failed to answer — genuinely transient, and excluding it
    /// would turn a nameserver hiccup into a hard failure. Pure, so both predicates above can be
    /// asserted without a socket.</para>
    /// </summary>
    /// <param name="exception">The outcome's exception, if any.</param>
    /// <returns><c>true</c> when the name could not be resolved because it does not exist.</returns>
    internal static bool NameDoesNotResolve(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is System.Net.Sockets.SocketException
                { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound })
                return true;
        return false;
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
                    // 🚨 The platform's OWN meter (#3488). Until it existed, every counter any
                    // investigation used was a runtime built-in standing in for what was actually
                    // wanted, and answering "how many hubs, of what kind, in what run level" cost
                    // a heap dump — which suspends the replica past its liveness budget and
                    // therefore RESTARTS it, destroying the state being measured. A meter nobody
                    // collects is the same silence one step later, so the name is subscribed here
                    // rather than left for a deployment to remember.
                    .AddMeter(MeshWeaver.Messaging.PlatformMetrics.MeterName)
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

        // 🚨 The OTHER half. Subscribing the meter NAME above only says "collect this if it
        // exists"; something must construct the instance, and the gauge closes over the root hub.
        // Both halves live here, in the host that actually collects — deliberately NOT in
        // MeshHostApplicationBuilder, which would arm a meter inside every mesh in the fleet,
        // including the several thousand a test run builds, none of which has a collector.
        builder.Services.AddPlatformMetrics();

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
                [ProbeEndpoints.LiveTag, ProbeEndpoints.ReadyTag])
            // What this replica could not TYPE (2026-09-08): a node whose content type is not
            // loaded here renders empty, and until now /health said only "Degraded". No probe
            // tag — Degraded on purpose, never a reason to pull the pod — so it reads on
            // ProbeEndpoints.Health alone, with the node types named in the detail.
            .AddCheck<ContentTypeHealthCheck>(ContentDegradationRegistry.HealthCheckName)
            // The volume the assembly store publishes into (2026-09-08: /data at 3 MiB free for
            // hours, the only symptom a NodeType that would not activate). No probe tag — Degraded
            // on purpose, never a reason to pull the pod — read on ProbeEndpoints.Health alone,
            // with the path and the numbers in the detail.
            .AddCheck<StorageCapacityHealthCheck>(StorageCapacityHealth.HealthCheckName)
            // How full the data volume is (2026-09-08): the share holding the prebuilt bundles,
            // the modules, the assembly cache and the DataProtection keys reached 3 MiB free and
            // every write on it failed far from the cause. Degraded below DataVolume:MinimumFreeBytes
            // (1 GiB) on the volume of any configured store root — no probe tag, pulling the pod
            // frees nothing — naming the path, used and total.
            .AddCheck(DataVolumeFreeSpace.HealthCheckName, new DataVolumeHealthCheck(builder.Configuration))
            // 🚨 The two bake verdicts that existed ONLY in a boot log (#3703, #3704). Both carry
            // ProbeEndpoints.CensusTag and NO probe tag: a census publishes a NUMBER, so it prints
            // on ProbeEndpoints.Health whatever its status, and can never restart a pod or take one
            // out of rotation. Registered UNCONDITIONALLY, deliberately — nodetype_bake is behind
            // `if (gateBake)` in the image host, which is right for a READINESS gate and would be
            // exactly wrong here: an instrument that is absent precisely where pre-warming is off
            // answers nothing about the deployments that most need it.
            //
            // #3703: which of this replica's NodeTypes the share already holds, and how many were
            // classified from a record this process had itself just written — Degraded when there
            // is no report at all, because a missing measurement may not read as a clean one.
            .AddCheck<BakeReportHealthCheck>(
                NodeTypeBakeReportRegistry.HealthCheckName, tags: [ProbeEndpoints.CensusTag])
            // #3704: the batched source discovery's chunk count and largest inter-chunk gap against
            // the completion window — the discriminator between "the completion rule ended the fold
            // early" and "the providers returned less". Healthy when no pass ran (the normal state
            // of a warm replica) and it still PRINTS, which is the whole point of the census tag.
            .AddCheck<SourceDiscoveryHealthCheck>(
                SourceDiscoveryRegistry.HealthCheckName, tags: [ProbeEndpoints.CensusTag])
            // #4063: whether this identity still has a publication of each module-bearing
            // repository at or after its last green build. A publication seal that stops advancing
            // freezes every GitSynced Space of that repository — silently, because a frozen source
            // and a settled one were field-for-field identical until #4065, and because a freeze
            // looks exactly like a quiet week from outside. Census-tagged for that reason: the
            // CLEAN reading is the publication, so "nothing measured", "no CI bakes here", "none
            // held" and "held for nine hours" are four different printed sentences. Degraded only
            // past the fleet's 45-minute CI job cap, where the ordinary webhook-before-seal
            // ordering can no longer explain the hold.
            .AddCheck<SealedSyncHealthCheck>(
                SealedSyncCensus.HealthCheckName, tags: [ProbeEndpoints.CensusTag]);

        // 🚨 A roll gate holds READINESS only (policy bake-gate-readiness-only, #5544). The host
        // registers the NodeType bake gate by NAME, in another repository, and until this change it
        // carried no tag at all — so it landed on /health, the startup probe read it, and a refusal
        // killed every container of every image at the end of the startup budget. Tagging it HERE,
        // after every host's own registration (PostConfigure runs after all Configure calls), makes
        // the rule hold whatever tags the host gave it: no host can put a roll gate back on the
        // startup probe by forgetting a tag.
        builder.Services.PostConfigure<HealthCheckServiceOptions>(TagRollGates);

        return builder;
    }

    /// <summary>
    /// The health checks that are ROLL GATES by name (see <see cref="ProbeEndpoints.RollGateTag"/>):
    /// their verdict is read by <see cref="ProbeEndpoints.Ready"/> alone, never by the startup
    /// probe on <see cref="ProbeEndpoints.Health"/>.
    ///
    /// <para>Two checks: the NodeType bake gate (policy <c>bake-gate-readiness-only</c>) and the
    /// required-modules check (policy <c>required-modules-readiness-only</c>). Its Unhealthy verdict
    /// (a required module absent or not installable here) is a property of what the pod's shelf was
    /// given, and when the registry cannot serve the module it is missing for every image — so on
    /// the startup probe it would kill the previous image's restarted pods too. As a roll gate it
    /// stalls the roll and keeps the pod out of the Service instead. (Its Degraded verdict — a
    /// store-delivered module not here yet — is a 200 on every probe and holds nothing.) The other checks a portal registers stay on the startup probe deliberately
    /// — the per-check decision and its reasoning are in
    /// Doc/Architecture/TheBakeGateOnlyStallsARoll ("Which checks may fail the startup probe").</para>
    /// </summary>
    internal static readonly ImmutableHashSet<string> RollGateChecks =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            NodeTypeBakeGateExtensions.HealthCheckName,
            ProbeEndpoints.RequiredModulesCheckName);

    /// <summary>
    /// Adds <see cref="ProbeEndpoints.RollGateTag"/> to every registration named in
    /// <see cref="RollGateChecks"/>, and removes <see cref="ProbeEndpoints.LiveTag"/> from it: a
    /// roll gate that restarted the pod would be the same container death on a different probe.
    /// </summary>
    /// <param name="options">The health-check options every host registration has configured.</param>
    internal static void TagRollGates(HealthCheckServiceOptions options)
    {
        foreach (var registration in options.Registrations)
        {
            if (!RollGateChecks.Contains(registration.Name))
                continue;
            registration.Tags.Add(ProbeEndpoints.RollGateTag);
            registration.Tags.Remove(ProbeEndpoints.LiveTag);
        }
    }

    /// <summary>
    /// The <c>/health</c> body: the aggregate status on the first line — exactly what it was,
    /// so nothing that reads the first word changes — then one line per check that is not
    /// Healthy, naming it and its description. Before this the endpoint answered the bare word
    /// <c>Degraded</c>, and finding WHICH check meant reading pod logs.
    ///
    /// <para>🚨 <b>…plus every check tagged <see cref="ProbeEndpoints.CensusTag"/>, Healthy or
    /// not</b> (#3703, #3704). "Print only what is wrong" is right for a VERDICT and wrong for a
    /// CENSUS, where the READING is the publication: a census check that answered
    /// Healthy-and-silent would be byte-identical on the wire to one that was never registered, so
    /// "I measured nothing" and "I measured, and it was clean" could not be told apart. That is
    /// precisely the ambiguity that left both issues unanswerable while their numbers sat in a
    /// boot log. The tag is opt-in and additive: no existing entry's behaviour changes, and the
    /// aggregate status word is untouched — a clean census stays Healthy and does not paint the
    /// replica Degraded.</para>
    /// </summary>
    internal static Task WriteHealthWithDetail(HttpContext context, HealthReport report)
    {
        // 🚨 The status CODE and the word on line one are the STARTUP verdict: every check EXCEPT
        // a roll gate (policy bake-gate-readiness-only, #5544). The middleware set the code from
        // the aggregate, which includes the roll gates; the response has not started yet, so the
        // code is corrected here, before the first byte.
        var startup = StartupStatus(report);
        context.Response.StatusCode = startup == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(string.Join('\n', HealthBodyLines(report, startup)));
    }

    /// <summary>
    /// The startup verdict: the worst status over every entry that is NOT a roll gate. A roll
    /// gate's verdict belongs to readiness; on the startup probe it would kill the container.
    /// </summary>
    /// <param name="report">The report the probe just produced.</param>
    /// <returns>The status the startup probe reads.</returns>
    internal static HealthStatus StartupStatus(HealthReport report) =>
        report.Entries.Values
            .Where(e => !e.Tags.Contains(ProbeEndpoints.RollGateTag))
            .Select(e => e.Status)
            .DefaultIfEmpty(HealthStatus.Healthy)
            .Min();

    /// <summary>
    /// The <see cref="ProbeEndpoints.Health"/> body. Pure, so a test can pin it without a socket.
    /// A roll gate's reading ALWAYS prints, Healthy or not, marked as read by readiness only: the
    /// operator still reads the gate on <c>/health</c>, and "armed and green" reads differently
    /// from "not registered".
    /// </summary>
    /// <param name="report">The report the probe just produced.</param>
    /// <param name="startup">The startup verdict, printed on line one.</param>
    /// <returns>The body's lines, in order.</returns>
    internal static ImmutableList<string> HealthBodyLines(HealthReport report, HealthStatus startup)
    {
        var lines = ImmutableList.Create(startup.ToString(), TimingLine(report));
        foreach (var (name, entry) in report.Entries)
        {
            var rollGate = entry.Tags.Contains(ProbeEndpoints.RollGateTag);
            if (entry.Status == HealthStatus.Healthy && !rollGate && !entry.Tags.Contains(ProbeEndpoints.CensusTag))
                continue;
            lines = lines.Add($"{name}: {entry.Status}"
                + (string.IsNullOrEmpty(entry.Description) ? "" : $" — {entry.Description}")
                + (rollGate ? $" [roll gate: read by {ProbeEndpoints.Ready} only, never by the startup probe]" : ""));
        }
        return lines;
    }

    /// <summary>
    /// The <see cref="ProbeEndpoints.Ready"/> body: the status word, then one line per check that
    /// is not Healthy. A pod held out of the Service by a roll gate then says WHY in the kubelet's
    /// probe-failure event, instead of a bare <c>Unhealthy</c>.
    /// </summary>
    /// <param name="context">The probe request.</param>
    /// <param name="report">The report over the readiness predicate.</param>
    /// <returns>The write.</returns>
    internal static Task WriteReadyWithDetail(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        var lines = ImmutableList.Create(report.Status.ToString())
            .AddRange(report.Entries
                .Where(e => e.Value.Status != HealthStatus.Healthy)
                .Select(e => $"{e.Key}: {e.Value.Status}"
                    + (string.IsNullOrEmpty(e.Value.Description) ? "" : $" — {e.Value.Description}")));
        return context.Response.WriteAsync(string.Join('\n', lines));
    }

    /// <summary>
    /// A check slower than this is NAMED individually on <see cref="ProbeEndpoints.Health"/>; the
    /// rest are counted, not dropped.
    ///
    /// <para>🚨 It is a NAMING threshold, not a claim about what can explain a timeout. Enough
    /// checks just below it would consume the budget between them, and that is exactly why the
    /// TOTAL is published first and unconditionally: the aggregate always includes them, so the
    /// reading "total 9412ms, nothing named" is itself an answer — the cost is spread, look at the
    /// count, not for one culprit. What the threshold buys is that the line does not grow with
    /// every check that costs nothing.</para>
    /// </summary>
    internal const double TimingNamedAboveMs = 10;

    /// <summary>
    /// 🚨 <b>What the probe's own endpoint SPENT, published on it</b> (MeshWeaver#4588).
    ///
    /// <para>The chart reads <see cref="ProbeEndpoints.Health"/> as the <c>startupProbe</c>, and that
    /// is the one probe whose failure is not recoverable: a container that never records a success
    /// never leaves startup, is never Ready, and is killed when
    /// <c>periodSeconds x failureThreshold</c> runs out — then repeats. So once this endpoint's own
    /// LATENCY passes the probe's <c>timeoutSeconds</c>, the verdict stops mattering: the
    /// instrument, not the health of the pod, decides the rollout.</para>
    ///
    /// <para><b>Measured 2026-09-17 on memex.systemorph.com</b>, from outside, three consecutive
    /// reads: <c>/health</c> answered 200 in 8.12 s, 9.62 s and 9.52 s while <c>/alive</c> and
    /// <c>/ready</c> on the same host and pod answered in 0.12 s — so the seconds were entirely in
    /// the untagged checks. That instance gives the startup probe <c>timeoutSeconds: 5</c>, so every
    /// probe ran out of time before the endpoint could answer. The replica rolled onto
    /// 3.0.0-ci.8812 at 11:23:40Z was still not Ready at 14:51Z and had been killed once at almost
    /// exactly its 3 h budget (<c>periodSeconds: 10</c> x <c>failureThreshold: 1080</c>), with the
    /// bake gate GREEN throughout — the aggregate word on line one was <c>Degraded</c>, which is a
    /// 200 and therefore a passing verdict.</para>
    ///
    /// <para>🚨 <b>And nothing could say WHICH check spent it.</b> The framework logs a per-check
    /// duration, but a check that answers <see cref="HealthStatus.Healthy"/> logs it at Information,
    /// which this fleet filters out of Loki for the <c>Microsoft.*</c> categories (measured: not one
    /// line matching <c>with status Healthy</c> has ever reached the log store). So the slow check
    /// was, by construction, the one kind of check no reader could name. The report carries
    /// <see cref="HealthReport.TotalDuration"/> and every entry's
    /// <see cref="HealthReportEntry.Duration"/> and this writer dropped both — the same shape
    /// #3703/#3704 fixed for the bake and discovery readings, on the endpoint's own cost. #4588's
    /// own attribution caveat is exactly this hole — <i>"the fleet watch reports /health unreachable
    /// … TaskCanceledException for that pod, and for one of the two healthy ci.8710 pods as well, so
    /// that signal does not separate them"</i> — and it does not separate them because BOTH are over
    /// that watch's 8 s budget for a reason this endpoint never stated.</para>
    ///
    /// <para>Placed on line TWO, directly under the status word: a reader of a truncated body (the
    /// fleet watch keeps the first 2000 characters) needs the timing before any description, and
    /// line one stays exactly the bare status word every caller parses. Pure, so
    /// <c>HealthTimingIsPublishedTest</c> can pin it without a socket.</para>
    /// </summary>
    /// <param name="report">The report the probe just produced.</param>
    /// <returns>The one timing line.</returns>
    internal static string TimingLine(HealthReport report)
    {
        // InvariantCulture throughout: this body is an operator/machine payload with no viewer
        // locale, and its numbers are compared across replicas and pasted into issues.
        var floor = TimingNamedAboveMs.ToString("F0", CultureInfo.InvariantCulture);
        var header = "timing: "
            + report.TotalDuration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)
            + $"ms total over {report.Entries.Count} check(s)";
        if (report.Entries.Count == 0)
            return $"{header} — none registered, so this endpoint measures NOTHING";

        var named = report.Entries
            .Select(e => (e.Key, Ms: e.Value.Duration.TotalMilliseconds))
            .Where(e => e.Ms >= TimingNamedAboveMs)
            .OrderByDescending(e => e.Ms)
            .ToImmutableArray();
        var rest = report.Entries.Count - named.Length;
        if (named.Length == 0)
            return $"{header} — all {rest} under {floor}ms";

        var slowest = string.Join("; ",
            named.Select(e => $"{e.Key} {e.Ms.ToString("F0", CultureInfo.InvariantCulture)}ms"));
        return rest == 0
            ? $"{header}, slowest first — {slowest}"
            : $"{header}, slowest first — {slowest}; {rest} more under {floor}ms";
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.UseRequestTimeouts();

        // Every check RUNS here; every check except a roll gate decides the startup verdict. The
        // body names every check that is not Healthy, plus every census and roll-gate reading
        // (WriteHealthWithDetail) — the startup status word stays on line one.
        app.MapHealthChecks(ProbeEndpoints.Health,
            new HealthCheckOptions { ResponseWriter = WriteHealthWithDetail });

        // Only health checks tagged with "live" must pass for app to be considered alive
        app.MapHealthChecks(ProbeEndpoints.Live,
            new HealthCheckOptions { Predicate = r => r.Tags.Contains(ProbeEndpoints.LiveTag) });

        // Only health checks tagged with "ready" decide whether this pod stays in the Service —
        // plus the ROLL GATES, whose verdict is read here and nowhere else (policy
        // bake-gate-readiness-only): refusing readiness stalls a roll with the previous image
        // serving and kills nothing.
        app.MapHealthChecks(ProbeEndpoints.Ready,
            new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains(ProbeEndpoints.ReadyTag) || r.Tags.Contains(ProbeEndpoints.RollGateTag),
                ResponseWriter = WriteReadyWithDetail,
            });

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
/// <param name="Version">The platform version (<c>3.0.0-ci.{run}</c> for CI builds).</param>
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
