using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Kicks <see cref="DynamicTypePreWarmer.WarmDynamicTypes"/> once, in the background, after
/// the host has fully started (so the Orleans silo is up and grains can activate).
///
/// <para>Registered via <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>
/// — opt-in, portal-only. It is NOT wired into the shared <see cref="MeshHostApplicationBuilder"/>
/// so tests and non-portal hosts never pay for a startup warm-up they don't want.</para>
///
/// <para><b>Never blocks host startup or readiness.</b> <see cref="StartAsync"/> returns
/// immediately; the warm-up is launched from the <c>ApplicationStarted</c> callback and runs
/// on a background Rx subscription. The subscription is torn down on shutdown.</para>
///
/// <para><b>OFF by default</b> (<c>PreWarm:DynamicTypes</c> config, default <c>false</c>): the
/// warm-up enumerates EVERY NodeType across EVERY partition and activates + compiles each dynamic
/// one — on a mesh with many partitions that is a boot-time subscribe/compile storm that starves
/// the hubs (and, multi-silo, the Orleans membership probes — the "I have been told I am dead"
/// restart loop on memex, 2026-07-22). Correctness never needs it: first access compiles lazily
/// via <c>NodeTypeEnrichmentHelpers.WaitForCompileSettled</c>. Opt in only where the
/// first-visitor compile latency actually hurts and the mesh is small enough to warm quietly.</para>
/// </summary>
public sealed class DynamicTypePreWarmerHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DynamicTypePreWarmerHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>Config key that opts a deployment into the startup warm-up (default: off).</summary>
    public const string EnabledConfigKey = "PreWarm:DynamicTypes";

    private IDisposable? _warmSubscription;
    private IDisposable? _startedRegistration;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStarted fires only after EVERY hosted service (incl. the Orleans silo)
        // has started — so grains can activate. Registering the kick here, rather than doing
        // work in StartAsync, guarantees the silo is up without any ordering assumptions.
        _startedRegistration = lifetime.ApplicationStarted.Register(KickWarmup);
        return Task.CompletedTask;
    }

    private void KickWarmup()
    {
        // The bake barrier consumers sequence on (#1114) — settled at EVERY terminal of this
        // method, including the early no-bake returns, so a waiter never waits on a bake that is
        // not coming. Resolved (not required) for resilience; AddDynamicTypePreWarming registers it.
        var bake = services.GetService<PreWarmCompletion>();

        // Config-gated, DEFAULT OFF — see the class doc. Read as a raw string (no Binder
        // dependency); anything but an explicit true stays lazy.
        var enabled = services.GetService<IConfiguration>()?[EnabledConfigKey];
        if (!bool.TryParse(enabled, out var isEnabled) || !isEnabled)
        {
            logger.LogInformation(
                "DynamicTypePreWarmer: disabled ({Key} != true) — dynamic NodeTypes compile lazily on first access",
                EnabledConfigKey);
            bake?.MarkSettled();
            return;
        }

        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
        {
            logger.LogDebug("DynamicTypePreWarmer: no mesh hub resolved — skipping startup warm-up");
            bake?.MarkSettled();
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var compiled = 0;
        var alreadyBaked = 0;
        var errored = 0;
        var timedOut = 0;
        var skipped = 0;
        var faulted = 0;

        // The readiness gate reads this. Resolved (not required) so a host that never called
        // AddNodeTypeBakeGate simply warms without gating — the warmer stays a latency optimisation
        // unless a deployment explicitly opts into making it a rollout gate.
        var gate = services.GetService<NodeTypeBakeGateState>();
        gate?.MarkRunning("enumerating dynamic NodeTypes");

        logger.LogInformation("DynamicTypePreWarmer: starting background warm-up of dynamic NodeType hubs");
        _warmSubscription = DynamicTypePreWarmer
            .WarmDynamicTypes(mesh, logger)
            .Subscribe(
                outcome =>
                {
                    gate?.MarkOutcome(outcome);
                    switch (outcome.Status)
                    {
                        case PreWarmStatus.Compiled: Interlocked.Increment(ref compiled); break;
                        case PreWarmStatus.AlreadyBaked: Interlocked.Increment(ref alreadyBaked); break;
                        case PreWarmStatus.CompileError: Interlocked.Increment(ref errored); break;
                        case PreWarmStatus.TimedOut: Interlocked.Increment(ref timedOut); break;
                        // A type skipped because its upstream failed — or because its upstream was
                        // never evaluated — is not a FAULT; it is a deliberate, reported outcome.
                        // Counting it as one made the summary read like the warmer had crashed N
                        // times when one dependency was broken. (Which of the two it was is not
                        // lost: the gate files UpstreamUnevaluated under "not evaluated" and names
                        // it in the health payload, while UpstreamFailed gates.)
                        case PreWarmStatus.UpstreamFailed:
                        case PreWarmStatus.UpstreamUnevaluated: Interlocked.Increment(ref skipped); break;
                        default: Interlocked.Increment(ref faulted); break;
                    }
                },
                ex =>
                {
                    logger.LogWarning(ex, "DynamicTypePreWarmer: warm-up stream faulted (best-effort — lazy compile still works)");
                    // A faulted sweep proved nothing. Release the gate rather than hold the pod out
                    // of rotation forever on a stream error: the lazy compile path still works, so
                    // an un-provable bake must not become an outage. A genuine broken type is caught
                    // by MarkOutcome above, which is what the gate is actually for.
                    gate?.MarkComplete("warm-up stream faulted — gate released, lazy compile applies");
                    // A fault is a terminal too: the sweep is over, the compile queue is no longer
                    // saturated, and whoever sequenced on the bake may proceed (#1114).
                    bake?.MarkSettled();
                },
                () =>
                {
                    var elapsed = DateTimeOffset.UtcNow - startedAt;
                    logger.LogInformation(
                        "DynamicTypePreWarmer: warm-up complete in {Elapsed} — compiled={Compiled} alreadyBaked={AlreadyBaked} "
                        + "compileErrors={Errored} timedOut={TimedOut} skipped={Skipped} faulted={Faulted}",
                        elapsed,
                        Volatile.Read(ref compiled), Volatile.Read(ref alreadyBaked), Volatile.Read(ref errored),
                        Volatile.Read(ref timedOut), Volatile.Read(ref skipped), Volatile.Read(ref faulted));

                    // MarkComplete keeps a recorded regression red — completion is not absolution.
                    gate?.MarkComplete(
                        $"baked in {elapsed:hh\\:mm\\:ss} — compiled={Volatile.Read(ref compiled)} "
                        + $"alreadyBaked={Volatile.Read(ref alreadyBaked)}");
                    // 🚨 Say ONLY what is actually enforced. The gate STATE is registered
                    // unconditionally, so this branch runs whether or not a readiness probe consumes
                    // it. Claiming a stall that nothing enforces is worse than saying nothing: it was
                    // read as proof the portal was protected while the pod went Ready and served
                    // traffic, and a production outage was diagnosed against it for hours.
                    if (gate is { Phase: BakePhase.Regressed })
                    {
                        var regressions = string.Join(" | ", gate.Regressions.Select(r => $"{r.Key} → {r.Value}"));
                        if (gate.GatesReadiness)
                            logger.LogCritical(
                                "DynamicTypePreWarmer: REFUSING READINESS — {Detail}. The rollout will stall "
                                + "with the previous image still serving. Regressions: {Regressions}",
                                gate.Detail, regressions);
                        else
                            logger.LogCritical(
                                "DynamicTypePreWarmer: GATE NOT ARMED — {Count} NodeType(s) regressed on this "
                                + "image and NOTHING CONSUMES THIS STATE, so nothing is blocked. Startup "
                                + "continues and instances of these types will fail. To make it enforce, run a "
                                + "host that registers the bake readiness check with '{Key}'=true, and give the "
                                + "startupProbe a full cold-bake budget. {Detail}. Regressions: {Regressions}",
                                gate.Regressions.Count, NodeTypeBakeGateExtensions.EnabledConfigKey,
                                gate.Detail, regressions);
                    }

                    // The sweep ran to its end — regressed or not, the compile queue has drained
                    // and per-node hub activations no longer park behind it. Release the boot
                    // flows sequenced on the bake (#1114). On a Regressed+armed pod readiness
                    // stays refused regardless; the default install proceeding is deliberate —
                    // installs repair content, and the broken type is already terminal.
                    bake?.MarkSettled();
                });
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _startedRegistration?.Dispose();
        _startedRegistration = null;
        _warmSubscription?.Dispose();
        _warmSubscription = null;
    }
}

/// <summary>Opt-in registration for the dynamic-NodeType startup pre-warm (portal hosts).</summary>
public static class PreWarmServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DynamicTypePreWarmerHostedService"/> so the pod CAN front-load its
    /// dynamic NodeType compiles at startup (Part 1 of the fresh-pod compile-race hardening).
    /// The warm-up only actually runs when the deployment opts in via
    /// <see cref="DynamicTypePreWarmerHostedService.EnabledConfigKey"/> (default: off — startup
    /// does no mesh-wide enumeration; types compile lazily on first access). Best-effort and
    /// non-blocking — safe to call from any portal host; a no-op if no mesh hub is present.
    /// </summary>
    public static IServiceCollection AddDynamicTypePreWarming(this IServiceCollection services)
    {
        // The bake barrier (#1114): boot flows that write through per-node hubs — the plugin
        // default install foremost — resolve this and sequence themselves after the sweep, so
        // they never race a post-roll recompile storm. Registered HERE, with the pre-warmer,
        // because its absence is itself the signal: a host without the pre-warm has no bake to
        // wait for, and consumers proceed immediately.
        services.TryAddSingleton<PreWarmCompletion>();
        services.AddHostedService<DynamicTypePreWarmerHostedService>();
        return services;
    }
}
