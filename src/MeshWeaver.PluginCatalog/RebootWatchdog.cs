using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>What one watchdog pass decided — open string constants.</summary>
public static class RebootWatchdogDecision
{
    /// <summary>The wedge predicate does not hold.</summary>
    public const string Healthy = "Healthy";

    /// <summary>The predicate holds and a self-reboot was filed.</summary>
    public const string Fired = "Fired";

    /// <summary>The predicate holds and the rate limit refused a self-reboot — alarmed.</summary>
    public const string Suppressed = "Suppressed";

    /// <summary>The predicate holds but the reboot could not be filed — alarmed.</summary>
    public const string Refused = "Refused";

    /// <summary>The watchdog is disabled on this instance.</summary>
    public const string Disabled = "Disabled";
}

/// <summary>One watchdog pass's answer.</summary>
/// <param name="Decision">A <see cref="RebootWatchdogDecision"/> value.</param>
/// <param name="Detail">The evidence and, when not fired, why.</param>
/// <param name="Path">The reboot request it filed, or null.</param>
public sealed record RebootWatchdogPass(string Decision, string Detail, string? Path = null);

/// <summary>
/// 🚨 The instance's own watchdog (<c>Doc/Architecture/InstanceReboot</c> → "Self-trigger"): every
/// <see cref="InstanceRebootOptions.WatchdogInterval"/> it reads the wedge evidence of THIS instance —
/// the load/binding faults components reported (<see cref="WedgeSignals"/>) and the critical NodeTypes
/// in compile Error — applies the EXPLICIT predicate (<see cref="RebootWatchdogRules.Evaluate"/>) and
/// the rate limit (<see cref="RebootWatchdogRules.Admit"/>), and files ONE reboot through
/// <see cref="InstanceReboot.Request"/> as <see cref="InstanceRebootTrigger.Watchdog"/>. No approval
/// (nobody is there to give one when the instance is wedged), but RATE-LIMITED and ALARMED: every
/// firing and every suppressed firing is a <c>Critical</c> log line naming the evidence. A
/// mesh-scoped singleton: its compile-error map is instance state.
/// </summary>
public sealed class RebootWatchdog
{
    private ImmutableDictionary<string, DateTimeOffset> compileErrorSince = ImmutableDictionary<string, DateTimeOffset>.Empty;
    private string? lastAlarm;
    private long lastFiredTicks;

    /// <summary>The clock. Production reads UTC now; a test moves it.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The name a self-reboot is requested by.</summary>
    public const string RequesterName = "reboot-watchdog";

    /// <summary>Arms the watchdog on the mesh hub of this process.</summary>
    internal IObservable<Unit> Arm(IMessageHub meshHub)
    {
        var options = meshHub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        if (!options.WatchdogEnabled || options.WatchdogInterval <= TimeSpan.Zero)
            return Observable.Return(Unit.Default);
        var logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<RebootWatchdog>();
        meshHub.RegisterForDisposal(meshHub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started)
            .SelectMany(_ => Observable.Interval(options.WatchdogInterval))
            .Select(_ => Evaluate(meshHub).Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[RebootWatchdog] a pass faulted; the next pass evaluates again");
                return Observable.Empty<RebootWatchdogPass>();
            }))
            .Concat()
            .Subscribe(_ => { }, ex => logger?.LogError(ex, "[RebootWatchdog] stopped on {Hub}", meshHub.Address)));
        return Observable.Return(Unit.Default);
    }

    /// <summary>One pass: read the evidence, decide, and file (or alarm). Cold; emits ONE pass.</summary>
    public IObservable<RebootWatchdogPass> Evaluate(IMessageHub meshHub)
    {
        var options = meshHub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        var logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<RebootWatchdog>();
        if (!options.WatchdogEnabled)
            return Observable.Return(new RebootWatchdogPass(RebootWatchdogDecision.Disabled, "the watchdog is disabled (Reboot:WatchdogEnabled)"));
        var signals = meshHub.ServiceProvider.GetService<WedgeSignals>()?.Snapshot() ?? ImmutableList<WedgeSignal>.Empty;
        return CriticalCompileErrors(meshHub, options)
            .SelectMany(inError =>
            {
                var now = Clock();
                // Passes run one at a time (Concat), so this read-fold-write has a single writer.
                var current = RebootWatchdogRules.FoldCompileErrors(Volatile.Read(ref compileErrorSince), inError, now);
                Volatile.Write(ref compileErrorSince, current);
                var verdict = RebootWatchdogRules.Evaluate(signals, current, now, options);
                if (verdict is null)
                {
                    Volatile.Write(ref lastAlarm, null);
                    return Observable.Return(new RebootWatchdogPass(RebootWatchdogDecision.Healthy, "the wedge predicate does not hold"));
                }
                // The listing of previous reboots trails the store by seconds, so THIS process also
                // remembers its own last firing — a pass right after a firing can never fire again.
                var firedHere = Interlocked.Read(ref lastFiredTicks);
                if (firedHere != 0 && now - new DateTimeOffset(firedHere, TimeSpan.Zero) < options.WatchdogMinInterval)
                {
                    var why = $"rate-limited: this process filed a self-reboot at {new DateTimeOffset(firedHere, TimeSpan.Zero):u} — at most one per "
                              + $"{options.WatchdogMinInterval.TotalHours:0.#} h; a person must act (reboot_instance or the Reboot button)";
                    Alarm(logger, verdict, $"WEDGED, self-reboot NOT taken — {why}");
                    return Observable.Return(new RebootWatchdogPass(RebootWatchdogDecision.Suppressed, $"{verdict.Evidence} — {why}"));
                }
                return PreviousReboots(meshHub).SelectMany(previous =>
                {
                    var (fire, why) = RebootWatchdogRules.Admit(verdict, previous, now, options);
                    if (!fire)
                    {
                        Alarm(logger, verdict, $"WEDGED, self-reboot NOT taken — {why}");
                        return Observable.Return(new RebootWatchdogPass(RebootWatchdogDecision.Suppressed, $"{verdict.Evidence} — {why}"));
                    }
                    return InstanceReboot.Request(meshHub, new InstanceRebootRequest
                        {
                            Reason = $"the instance's watchdog found it wedged: {verdict.Evidence}",
                            RequestedBy = RequesterName,
                            Trigger = InstanceRebootTrigger.Watchdog,
                            WedgeEvidence = verdict.Evidence,
                            WedgeFingerprint = verdict.Fingerprint,
                            Wedged = true,
                        })
                        .Select(ticket =>
                        {
                            if (!ticket.Accepted)
                            {
                                Alarm(logger, verdict, $"WEDGED, and the self-reboot could not be filed — {ticket.Refusal}");
                                return new RebootWatchdogPass(RebootWatchdogDecision.Refused, $"{verdict.Evidence} — {ticket.Refusal}");
                            }
                            Interlocked.Exchange(ref lastFiredTicks, now.UtcTicks);
                            // Always said, never deduplicated: a self-reboot is the event an operator must see.
                            logger?.LogCritical(
                                "[RebootWatchdog] WEDGED — SELF-REBOOT filed at {Path}: {Evidence} (fingerprint {Fingerprint}; "
                                + "at most one per {Hours} h)", ticket.Path, verdict.Evidence, verdict.Fingerprint,
                                options.WatchdogMinInterval.TotalHours);
                            return new RebootWatchdogPass(RebootWatchdogDecision.Fired, verdict.Evidence, ticket.Path);
                        });
                });
            });
    }

    /// <summary>A Critical alarm — once per distinct (evidence, reason) per process, so a pass every minute does not flood.</summary>
    private void Alarm(ILogger? logger, WedgeVerdict verdict, string sentence)
    {
        var key = $"{verdict.Fingerprint}|{sentence}";
        if (string.Equals(Interlocked.Exchange(ref lastAlarm, key), key, StringComparison.Ordinal))
            return;
        logger?.LogCritical("[RebootWatchdog] {Sentence}. Evidence: {Evidence} (fingerprint {Fingerprint})",
            sentence, verdict.Evidence, verdict.Fingerprint);
    }

    /// <summary>The critical NodeTypes in compile Error now, or null when the reading could not be made
    /// (which resets the "continuously" map — no evidence is not evidence of a wedge).</summary>
    private static IObservable<IReadOnlyCollection<string>?> CriticalCompileErrors(IMessageHub meshHub, InstanceRebootOptions options)
    {
        if (options.CriticalNamespaces.IsDefaultOrEmpty)
            return Observable.Return<IReadOnlyCollection<string>?>(ImmutableList<string>.Empty);
        var mesh = meshHub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        return options.CriticalNamespaces.ToObservable()
            .Select(ns => access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"namespace:{ns} scope:descendants nodeType:{MeshNode.NodeTypePath} content.compilationStatus:Error"))
                    .Take(1)
                    .Timeout(ActivationRecycle.ReadBudget))
                .Select(change => change.Items.Select(n => n.Path).ToImmutableList()))
            .Concat()
            .ToList()
            .Select(lists => (IReadOnlyCollection<string>?)lists.SelectMany(l => l).Distinct(StringComparer.Ordinal).ToImmutableList())
            .Catch((Exception _) => Observable.Return<IReadOnlyCollection<string>?>(null));
    }

    /// <summary>The reboots on record, from a LISTING of the request namespace. A listing that cannot be
    /// read answers one OPEN placeholder, which refuses the firing — the safe side for a reboot.</summary>
    private static IObservable<ImmutableList<RebootRecord>> PreviousReboots(IMessageHub meshHub)
    {
        var mesh = meshHub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{InstanceRebootRequest.Namespace} scope:children nodeType:{InstanceRebootRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .Select(change => change.Items
                .Select(n => (n.Path, Request: n.ContentAs<InstanceRebootRequest>(meshHub.JsonSerializerOptions)))
                .Where(x => x.Request is not null)
                .Select(x => new RebootRecord(x.Path, x.Request!.Trigger, x.Request.RequestedAt, x.Request.WedgeFingerprint,
                    InstanceRebootStatus.IsTerminal(x.Request.Status)))
                .ToImmutableList())
            .Catch((Exception ex) => Observable.Return(ImmutableList.Create(new RebootRecord(
                InstanceRebootRequest.Namespace, "unknown", DateTimeOffset.MinValue, null, false))
                .Select(r => r with { Path = $"{InstanceRebootRequest.Namespace} (unreadable: {ex.GetType().Name})" })
                .ToImmutableList()));
    }
}
