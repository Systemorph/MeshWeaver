using System.Reactive.Linq;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The release gate as a READABLE service: <c>GET /api/plugins/is-updatable?version=…</c> answers
/// #1754's question — <i>may this environment be rolled to that release?</i> — for the instance
/// serving the request.
///
/// <para>It exists because the verdict has to be reachable by the paths that roll a version but do
/// not run inside the portal: CD's own post-promote assertion, and an operator about to
/// <c>kubectl set image</c>. Those paths must not re-derive the rule — the whole point of
/// <see cref="ReleaseAvailability"/> is that there is ONE rule — so they read the answer instead
/// of recomputing it. The portal's own poller calls the same
/// <see cref="ReleaseAvailabilityService"/> in-process.</para>
///
/// <para>🚨 <b>Auth is an instance key or a build granted <c>verify:combo</c></b>, the same <c>mwi_</c> gate as the bundle routes, and it
/// fails CLOSED: the response names installed packages and the reasons a release is unsafe, which
/// is deployment inventory, not public information.</para>
///
/// <para><b>Scope, stated rather than implied:</b> an instance answers for ITSELF. The registry
/// records that an instance exists (<c>InstanceAutoRegistrationService</c>) but not what it has
/// installed, so no third party can answer for it today; each environment is asked at its own URL.
/// When #1735's per-environment composition lands, the declared package set becomes answerable
/// centrally and <see cref="ReleaseAvailabilityService"/> is the one place that changes — the rule
/// above does not.</para>
/// </summary>
public static class ReleaseGateEndpoints
{
    /// <summary>Route the gate is mounted at.</summary>
    public const string Route = "/api/plugins/is-updatable";

    /// <summary>
    /// 🚨 Route the SELECTOR is mounted at (#3479): <c>GET /api/plugins/roll-target</c> — <i>which
    /// release should this environment be on?</i>
    ///
    /// <para><see cref="Route"/> can only confirm or deny a version the caller already chose, so
    /// every path that rolls had to choose "the newest" by itself and discover completeness
    /// afterwards. This answers the choice, which is the inversion #3479 asks for. Same auth, same
    /// scope, same shared predicate — this is the walk over it, not a second one.</para>
    /// </summary>
    public const string SelectRoute = "/api/plugins/roll-target";

    /// <summary>
    /// 🚨 Route the instance's COMBO is served at (#3544): <c>GET /api/plugins/combo</c> — <i>which
    /// modules, at which refs, does this instance actually run?</i>
    ///
    /// <para>It exists for the same reason as its two siblings and for one caller in particular:
    /// the combo GATE (<c>ComboVerificationGate</c>) consults a verdict that only an off-cluster
    /// producer can mint, because producing one needs docker, a materialisation root and repo
    /// credentials a portal pod does not have. That producer — <c>mw-combo-verify</c> — needs the
    /// instance's combo as its FIRST input, and until this route existed there was no way to get
    /// it out of a running portal: <c>InstanceComboReader</c> had no HTTP, MCP or layout surface at
    /// all, so nothing ever ran the verifier and every roll in the fleet was UNVERIFIED (#3544).</para>
    ///
    /// <para>🚨 <b>The bytes are the contract.</b> The body is an <see cref="InstanceCombo"/>
    /// serialized with <see cref="InstanceComboAssembler.Json"/> — the very options
    /// <c>mw-combo-verify</c> deserializes with — so the response IS <c>combo.json</c> and no caller
    /// has to reshape it. There is deliberately no lossy alternative: the nearest existing node
    /// (<c>Hosting/ModuleInventory</c>) drops <c>readAt</c>, <c>isComplete</c>, <c>caveats</c> and
    /// the per-module sync detail, and a verdict derived from it would be about something other
    /// than this instance's real module set.</para>
    ///
    /// <para>Auth is the same <c>mwi_</c> instance key, failing CLOSED, for the same reason: a
    /// combo names every module repository and pinned commit this deployment carries, which is
    /// deployment inventory rather than public information — strictly narrower than nothing, and
    /// the same class the two routes above already return.</para>
    /// </summary>
    public const string ComboRoute = "/api/plugins/combo";

    /// <summary>Accepts one compatibility verdict from an explicitly trusted build.</summary>
    public const string VerificationRoute = "/api/plugins/combo-verification";

    /// <summary>The local resource named by a build's <c>verify:combo</c> grant.</summary>
    public const string VerificationResource = "combo";

    /// <summary>Maps the instance-key-gated release gate. Call alongside <c>MapPluginBundles</c>.</summary>
    public static IEndpointRouteBuilder MapReleaseGate(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (HttpContext http, string? version, CancellationToken ct) =>
                Verdict(http, version, ct))
            .AllowAnonymous();
        endpoints.MapGet(SelectRoute, (HttpContext http, string? current, CancellationToken ct) =>
                Selection(http, current, ct))
            .AllowAnonymous();
        endpoints.MapGet(ComboRoute, (HttpContext http, CancellationToken ct) => Combo(http, ct))
            .AllowAnonymous();
        endpoints.MapPost(VerificationRoute, (HttpContext http, CancellationToken ct) =>
                RecordVerification(http, ct))
            .AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// The roll SELECTION for this instance. Authenticated exactly like <see cref="Verdict"/> and
    /// for the same reason: the answer names installed packages and the releases that cannot serve
    /// them, which is deployment inventory rather than public information.
    /// </summary>
    private static Task<IResult> Selection(HttpContext http, string? current, CancellationToken ct)
    {
        var authenticator = http.RequestServices
            .GetRequiredService<InstanceRegistryAuthenticator>();

        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ReleaseGateEndpoints));

        return authenticator.AuthenticateOutcome(http.Request.Headers.Authorization)
            .SelectMany(outcome => outcome.IsUnavailable
                ? Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger))
                : outcome.Instance is null && !CanVerify(outcome)
                    ? Observable.Return(Results.Json(
                        new { error = "A registered instance key or an authorized build identity is required." },
                        statusCode: StatusCodes.Status401Unauthorized))
                    : Choose(http, current))
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Roll selection faulted after the response had already been sent"),
                ct)!;
    }

    private static IObservable<IResult> Choose(HttpContext http, string? current) =>
        http.RequestServices.GetRequiredService<ReleaseAvailabilityService>()
            // 🚨 The running version is READ HERE, not taken from the caller: "different from
            // current ⇒ update" is a claim about what this instance runs, and a caller that could
            // assert it could ask for a rollback by lying. The query parameter is a diagnostic
            // override for an operator asking "what would you pick from there", never the default.
            .SelectRollTarget(
                string.IsNullOrWhiteSpace(current)
                    ? ShippedReleaseSeed.InstalledPlatformVersion
                    : current)
            .Select(outcome => Results.Json(new
            {
                environment = outcome.Summary,
                kind = outcome.Kind.ToString(),
                current = outcome.CurrentVersion,
                selected = outcome.SelectedVersion,
                shouldUpdate = outcome.ShouldUpdate,
                // 🚨 Distinguishes an availability failure from "we looked and nothing is
                // complete" — the same split IsUpdatable's `indeterminate` makes.
                indeterminate = outcome.IsIndeterminate,
                // 🚨 THE DENOMINATOR, always. A completeness answer whose expected count nobody can
                // read is one nobody can tell from a vacuous one.
                requiredPlugins = outcome.RequiredPlugins,
                satisfiedPlugins = outcome.SatisfiedPlugins,
                // 🚨 #3651 — what the selected release's verdict said without deciding on it,
                // and the packages it would recompile at boot.
                advisories = outcome.Advisories.IsDefault
                    ? Array.Empty<string>()
                    : outcome.Advisories.ToArray(),
                bootCompiles = outcome.BootCompiles.IsDefault
                    ? Array.Empty<string>()
                    : outcome.BootCompiles.ToArray(),
                declined = outcome.Declined
                    .Select(d => new
                    {
                        version = d.Version,
                        reason = d.Reason,
                        blockers = d.Blockers
                            .Select(b => new { package = b.Package, status = b.Kind.ToString(), reason = b.Reason })
                            .ToArray(),
                    })
                    .ToArray(),
            }));

    private static Task<IResult> Verdict(HttpContext http, string? version, CancellationToken ct)
    {
        var authenticator = http.RequestServices
            .GetRequiredService<InstanceRegistryAuthenticator>();

        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ReleaseGateEndpoints));

        return authenticator.AuthenticateOutcome(http.Request.Headers.Authorization)
            .SelectMany(outcome => outcome.IsUnavailable
                ? Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger))
                : outcome.Instance is null && !CanVerify(outcome)
                    ? Observable.Return(Results.Json(
                        new { error = "A registered instance key or an authorized build identity is required." },
                        statusCode: StatusCodes.Status401Unauthorized))
                    : Answer(http, version))
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Release gate for version '{Version}' faulted after the response had already been sent",
                    version),
                ct)!;
    }



    private static IObservable<IResult> Answer(HttpContext http, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Observable.Return(Results.Json(
                new { error = "Query parameter 'version' is required — the release to be rolled to." },
                statusCode: StatusCodes.Status400BadRequest));

        var service = http.RequestServices.GetRequiredService<ReleaseAvailabilityService>();
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ReleaseGateEndpoints));

        return service.IsUpdatable(version)
            .Select(verdict =>
            {
                if (!verdict.IsUpdatable)
                    // Information, not Debug: a held environment must be findable in the logs
                    // without turning anything on. A silent hold is the outage this gate exists
                    // to prevent, not a quiet success.
                    logger?.LogInformation(
                        "Release gate: HOLD for {Version} — {Reason}", version, verdict.HoldReason);

                return Results.Json(new
                {
                    version,
                    isUpdatable = verdict.IsUpdatable,
                    // Distinguishes "the gate does not apply here" from "the gate passed" — the
                    // two must never render as the same tick.
                    enforced = verdict.NotEnforcedReason is null,
                    notEnforcedReason = verdict.NotEnforcedReason,
                    // 🚨 Distinguishes an availability failure from a compatibility verdict.
                    indeterminate = verdict.IsIndeterminate,
                    holdReason = verdict.HoldReason,
                    // 🚨 #3648 — what the installed modules DECLARE about the target, beside the
                    // verdict and never inside it: a declared floor the target does not rank
                    // above is an advisory, not a hold.
                    advisories = verdict.Advisories.IsDefault
                        ? Array.Empty<string>()
                        : verdict.Advisories.ToArray(),
                    // 🚨 #3651 — the packages the roll would Roslyn-compile at boot, by name: a
                    // cost the caller reads, never a reason in isUpdatable.
                    bootCompiles = verdict.BootCompiles.IsDefault
                        ? Array.Empty<string>()
                        : verdict.BootCompiles.ToArray(),
                    packages = verdict.Packages
                        .Select(p => new
                        {
                            package = p.Package,
                            status = p.Kind.ToString(),
                            reason = p.Reason,
                            // Whether the status is a cost the roll accepts rather than a hold.
                            advisory = p.IsAdvisory,
                        })
                        .ToArray(),
                });
            });
    }

    /// <summary>
    /// The instance's own combo, authenticated exactly like <see cref="Verdict"/> and
    /// <see cref="Selection"/>. See <see cref="ComboRoute"/> for why it exists and why the bytes
    /// are the contract.
    /// </summary>
    private static Task<IResult> Combo(HttpContext http, CancellationToken ct)
    {
        var authenticator = http.RequestServices
            .GetRequiredService<InstanceRegistryAuthenticator>();

        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ReleaseGateEndpoints));

        return authenticator.AuthenticateOutcome(http.Request.Headers.Authorization)
            .SelectMany(outcome => outcome.IsUnavailable
                ? Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger))
                : outcome.Instance is null && !CanVerify(outcome)
                    ? Observable.Return(Results.Json(
                        new { error = "A registered instance key or an authorized build identity is required." },
                        statusCode: StatusCodes.Status401Unauthorized))
                    : StateCombo(http, logger))
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Instance combo read faulted after the response had already been sent"),
                ct)!;
    }

    private static bool CanVerify(InstanceAuthResult outcome) =>
        outcome.Build?.Allows(BuildVerbs.Verify, VerificationResource) == true;

    private static Task<IResult> RecordVerification(HttpContext http, CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ReleaseGateEndpoints));
        return http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>()
            .AuthenticateOutcome(http.Request.Headers.Authorization)
            .SelectMany(outcome => outcome.IsUnavailable
                ? Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger))
                : !CanVerify(outcome)
                    ? Observable.Return(Results.Json(
                        new { error = "An authorized build identity is required." },
                        statusCode: StatusCodes.Status401Unauthorized))
                    : ReadAndRecordVerification(http, outcome.Build!, logger, ct))
            .FirstAsync()
            .ObserveCompletion(ex => logger?.LogWarning(ex,
                "Combo verification recording failed after the response was sent"), ct)!;
    }

    private static IObservable<IResult> ReadAndRecordVerification(
        HttpContext http, AuthenticatedBuild build, ILogger? logger, CancellationToken ct)
    {
        // Authenticate before reading the body or resolving any mesh service. The caller never
        // becomes System: only the existing, narrowly scoped RecordVerification primitive writes.
        var hub = http.RequestServices.GetRequiredService<IMessageHub>();
        var pool = hub.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Http);
        return pool.Invoke(_ => http.Request.ReadFromJsonAsync<ComboVerification>(
                InstanceComboAssembler.Json, ct).AsTask())
            .SelectMany(verdict =>
            {
                if (verdict is null || string.IsNullOrWhiteSpace(verdict.CandidateTag)
                    || verdict.VerifiedAt == default || !Enum.IsDefined(verdict.Verdict)
                    || verdict.Modules is null || verdict.Caveats is null
                    || verdict.Modules.Any(m => m is null || string.IsNullOrWhiteSpace(m.ModuleId)
                        || !Enum.IsDefined(m.Outcome) || m.Failures is null)
                    || verdict.Modules.Select(m => m.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() != verdict.Modules.Count
                    || (verdict.Verdict == ComboVerdictKind.Green
                        && (verdict.Modules.Count == 0 || string.IsNullOrWhiteSpace(verdict.ImageDigest)
                            || string.IsNullOrWhiteSpace(verdict.VerifiedPlatform)
                            || verdict.ComboReadAt == default
                            || verdict.Modules.Any(m => m.Outcome != ModuleVerificationOutcome.Passed))))
                    return Observable.Return(Results.BadRequest(new
                        { error = "A complete, internally consistent combo verification is required." }));

                var expected = JsonSerializer.Serialize(verdict, InstanceComboAssembler.Json);
                return UpdatePolicyNodeType.RecordVerification(hub, verdict)
                    // Update is optimistic across hubs. A 200 promises that the owning node has
                    // recorded this exact verdict, so observe its reconciled state before replying.
                    .SelectMany(_ => Observable.Create<IResult>(observer =>
                    {
                        using (AccessContextScope.AsSystem(hub.ServiceProvider.GetRequiredService<AccessService>()))
                            return hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                                .Select(node => UpdatePolicyNodeType.Parse(node, hub.JsonSerializerOptions)
                                    .VerificationFor(verdict.CandidateTag))
                                .Where(recorded => JsonSerializer.Serialize(recorded,
                                    InstanceComboAssembler.Json) == expected)
                                .Take(1)
                                .Select(_ => (IResult)Results.Ok(new
                                    { recorded = true, candidateTag = verdict.CandidateTag }))
                                .Subscribe(observer);
                    }))
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Do(_ => logger?.LogInformation(
                        "Build {Repository} run {RunId} recorded combo verdict {Verdict} for {Candidate}",
                        build.Repository, build.Claims.RunId, verdict.Verdict, verdict.CandidateTag));
            })
            .Catch<IResult, JsonException>(_ => Observable.Return(Results.BadRequest(new
                { error = "The combo verification body is not valid JSON." })));
    }

    private static IObservable<IResult> StateCombo(HttpContext http, ILogger? logger) =>
        http.RequestServices.GetRequiredService<InstanceComboReader>().Read()
            .Select(combo =>
            {
                // 🚨 Information, and stated rather than swallowed. The reader never faults — an
                // unreadable source sets IsComplete=false and a caveat — so an incomplete combo
                // would otherwise leave the portal silent while the verifier folds it into a
                // NotVerifiable nobody can attribute. THE DENOMINATOR travels with it.
                if (!combo.IsComplete)
                    logger?.LogInformation(
                        "Instance combo served INCOMPLETE: {Modules} module(s), caveats: {Caveats}",
                        combo.Modules.Count, string.Join(" | ", combo.Caveats));

                // The producer deserializes with these exact options, so this response IS
                // combo.json. Results.Json would re-serialize with ASP.NET's options instead.
                return (IResult)Results.Content(
                    JsonSerializer.Serialize(combo, InstanceComboAssembler.Json), "application/json");
            });
}
