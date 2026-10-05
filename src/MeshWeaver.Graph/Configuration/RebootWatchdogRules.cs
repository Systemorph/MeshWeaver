using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Options of the instance reboot and its watchdog (<c>Doc/Architecture/InstanceReboot</c>). A
/// mesh-scoped singleton; a host binds it from configuration (<c>Reboot</c> section) or keeps the
/// defaults, which are deliberately conservative.
/// </summary>
public sealed record InstanceRebootOptions
{
    /// <summary>The namespaces whose NodeTypes are critical: a bake failure there fails the reboot's
    /// verification, and a compile Error there is a leg of the watchdog's wedge predicate. The control
    /// instance's own operations surface is <c>Hosting</c>.</summary>
    public ImmutableArray<string> CriticalNamespaces { get; init; } = ImmutableArray.Create("Hosting");

    /// <summary>How long the verification waits for this process's NodeType bake to settle.</summary>
    public TimeSpan BakeSettleBudget { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long one verification check may take.</summary>
    public TimeSpan CheckBudget { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the watchdog may file a reboot by itself. On by default: it fires only on the
    /// explicit predicate (<see cref="RebootWatchdogRules.Evaluate"/>) and is rate-limited.</summary>
    public bool WatchdogEnabled { get; init; } = true;

    /// <summary>How often the watchdog evaluates its predicate.</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Thread-start leg: this many thread starts failing with a load/binding fault…</summary>
    public int ThreadStartFaultThreshold { get; init; } = 3;

    /// <summary>…within this window.</summary>
    public TimeSpan ThreadStartWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Compile leg: a critical NodeType seen in compile Error on EVERY evaluation for at least this long.</summary>
    public TimeSpan CompileErrorFor { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Rate limit: at most one SELF-triggered reboot per this interval.</summary>
    public TimeSpan WatchdogMinInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The same evidence that survived a self-reboot within this window does not fire another — a reboot did not clear it.</summary>
    public TimeSpan WatchdogRepeatWindow { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>The kinds of wedge signal a component reports — open string constants.</summary>
public static class WedgeSignalKinds
{
    /// <summary>A thread start failed (the incident's shape: a <c>MissingMethodException</c> on every thread start).</summary>
    public const string ThreadStart = "ThreadStart";
}

/// <summary>One load/binding fault a component reported.</summary>
/// <param name="Kind">A <see cref="WedgeSignalKinds"/> value.</param>
/// <param name="Source">Who reported it (a type, a path).</param>
/// <param name="Fault">The classified fault, one line (<see cref="RebootWatchdogRules.LoadOrBindingFault"/>).</param>
/// <param name="At">When.</param>
public sealed record WedgeSignal(string Kind, string Source, string Fault, DateTimeOffset At);

/// <summary>What the wedge predicate found: the evidence (one line) and its fingerprint.</summary>
public sealed record WedgeVerdict(string Evidence, string Fingerprint);

/// <summary>A previous reboot the rate limit reads: when, what started it, its fingerprint, whether it ended.</summary>
public sealed record RebootRecord(string Path, string Trigger, DateTimeOffset RequestedAt, string? Fingerprint, bool Terminal);

/// <summary>
/// 🚨 The load/binding faults a component reports for the reboot watchdog. A mesh-scoped singleton
/// (its lifetime IS the mesh's — never static): a bounded, time-ordered queue held in an instance
/// field. Only a fault <see cref="RebootWatchdogRules.LoadOrBindingFault"/> classifies is kept —
/// anything else (a model error, a validation refusal) is not evidence of a wedge and is dropped
/// here, so a caller can report every failure without pre-filtering.
/// </summary>
public sealed class WedgeSignals
{
    /// <summary>How many signals are kept (the oldest drop first).</summary>
    public const int Capacity = 256;

    private ImmutableList<WedgeSignal> signals = ImmutableList<WedgeSignal>.Empty;

    /// <summary>Records <paramref name="exception"/> when it is a load/binding fault; answers whether it was kept.</summary>
    public bool Report(string kind, string source, Exception exception, DateTimeOffset? at = null)
    {
        if (RebootWatchdogRules.LoadOrBindingFault(exception) is not { } fault)
            return false;
        var signal = new WedgeSignal(kind, source, fault, at ?? DateTimeOffset.UtcNow);
        ImmutableInterlocked.Update(ref signals, list =>
        {
            var next = list.Add(signal);
            return next.Count > Capacity ? next.RemoveRange(0, next.Count - Capacity) : next;
        });
        return true;
    }

    /// <summary>Every kept signal, oldest first.</summary>
    public ImmutableList<WedgeSignal> Snapshot() => Volatile.Read(ref signals);
}

/// <summary>Reporting a wedge signal from any hub — a no-op on a mesh that registers no watchdog.</summary>
public static class WedgeSignalExtensions
{
    /// <summary>
    /// Reports <paramref name="exception"/> as a <paramref name="kind"/> fault to the instance's reboot
    /// watchdog (<c>Doc/Architecture/InstanceReboot</c>). Kept only when it is a load/binding fault.
    /// Never throws.
    /// </summary>
    public static bool ReportWedgeFault(this IMessageHub hub, string kind, string source, Exception exception)
    {
        try
        {
            return hub.ServiceProvider.GetService<WedgeSignals>()?.Report(kind, source, exception) ?? false;
        }
        catch (ObjectDisposedException)
        {
            // A hub that is being disposed has no watchdog to report to; the fault itself is the
            // caller's to surface.
            return false;
        }
    }
}

/// <summary>
/// 🚨 The watchdog's rules, pure (<c>Doc/Architecture/InstanceReboot</c> → "Self-trigger"). EXPLICIT
/// and CONSERVATIVE: the instance reboots itself only on evidence that a reboot is the remedy — a
/// load/binding fault (the running process binds against an assembly it does not have) recurring on
/// thread starts, or a critical NodeType stuck in compile Error — never on a slow page, a model error
/// or a single fault. Each rule carries its negative control in <c>InstanceRebootWatchdogTest</c>.
/// </summary>
public static class RebootWatchdogRules
{
    /// <summary>
    /// The fault, as one line, when <paramref name="exception"/> (or an inner one) is a LOAD/BINDING
    /// fault — <see cref="MissingMemberException"/> (method, field), <see cref="TypeLoadException"/>,
    /// <see cref="BadImageFormatException"/>, or a <see cref="FileNotFoundException"/>/<see cref="FileLoadException"/>
    /// for an ASSEMBLY (a display name or a <c>.dll</c>) — else null. Unwraps aggregate, target-invocation
    /// and type-initialization wrappers. Pure.
    /// </summary>
    public static string? LoadOrBindingFault(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 8; depth++)
        {
            switch (exception)
            {
                case AggregateException aggregate:
                    foreach (var inner in aggregate.Flatten().InnerExceptions)
                        if (LoadOrBindingFault(inner) is { } fault)
                            return fault;
                    return null;
                case MissingMemberException or TypeLoadException or BadImageFormatException:
                    return Line(exception);
                case FileNotFoundException { FileName: { } file } when IsAssembly(file):
                    return Line(exception);
                case FileLoadException { FileName: { } file } when IsAssembly(file):
                    return Line(exception);
            }
            exception = exception is TargetInvocationException or TypeInitializationException or InvalidOperationException
                ? exception.InnerException
                : null;
        }
        return null;
    }

    private static bool IsAssembly(string file) =>
        file.Contains("Version=", StringComparison.Ordinal) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static string Line(Exception exception)
    {
        var message = string.Join(' ', (exception.Message ?? "").Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return $"{exception.GetType().Name}: {(message.Length <= 240 ? message : message[..240] + "…")}";
    }

    /// <summary>
    /// The wedge predicate. Fires when EITHER leg holds:
    /// <list type="bullet">
    /// <item><b>thread-start leg</b>: at least <see cref="InstanceRebootOptions.ThreadStartFaultThreshold"/>
    /// <see cref="WedgeSignalKinds.ThreadStart"/> load/binding faults within
    /// <see cref="InstanceRebootOptions.ThreadStartWindow"/> before <paramref name="now"/>;</item>
    /// <item><b>compile leg</b>: a critical NodeType (<see cref="InstanceRebootOptions.CriticalNamespaces"/>)
    /// observed in compile Error on every evaluation since at least
    /// <see cref="InstanceRebootOptions.CompileErrorFor"/> ago (<paramref name="criticalErrorSince"/>: type → first seen).</item>
    /// </list>
    /// Null when neither holds. Pure.
    /// </summary>
    public static WedgeVerdict? Evaluate(
        IReadOnlyCollection<WedgeSignal> signals,
        IReadOnlyDictionary<string, DateTimeOffset> criticalErrorSince,
        DateTimeOffset now,
        InstanceRebootOptions options)
    {
        var evidence = ImmutableList<string>.Empty;
        var fingerprint = ImmutableList<string>.Empty;

        var recent = signals
            .Where(s => s.Kind == WedgeSignalKinds.ThreadStart && s.At <= now && s.At > now - options.ThreadStartWindow)
            .OrderBy(s => s.At)
            .ToImmutableList();
        if (recent.Count >= options.ThreadStartFaultThreshold && options.ThreadStartFaultThreshold > 0)
        {
            var faults = recent.Select(s => s.Fault).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToImmutableList();
            evidence = evidence.Add(
                $"{recent.Count} thread start(s) failed with a load/binding fault in the last {options.ThreadStartWindow.TotalMinutes:0} min "
                + $"(latest from {recent[^1].Source}: {recent[^1].Fault})");
            fingerprint = fingerprint.AddRange(faults.Select(f => "thread|" + f));
        }

        var stuck = criticalErrorSince
            .Where(kv => now - kv.Value >= options.CompileErrorFor)
            .Select(kv => kv.Key)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToImmutableList();
        if (!stuck.IsEmpty)
        {
            evidence = evidence.Add(
                $"{stuck.Count} critical NodeType(s) in compile Error for at least {options.CompileErrorFor.TotalMinutes:0} min: {string.Join(", ", stuck.Take(10))}");
            fingerprint = fingerprint.AddRange(stuck.Select(p => "compile|" + p));
        }

        return evidence.IsEmpty
            ? null
            : new WedgeVerdict(string.Join("; ", evidence), Fingerprint(fingerprint));
    }

    /// <summary>A short, stable fingerprint of the evidence parts. Pure.</summary>
    public static string Fingerprint(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts.OrderBy(p => p, StringComparer.Ordinal)))))[..16]
            .ToLowerInvariant();

    /// <summary>
    /// Folds one evaluation's compile reading into the "in Error since" map: a critical type in
    /// <paramref name="inErrorNow"/> keeps its first-seen instant (or starts at <paramref name="now"/>);
    /// a type NOT in it leaves the map — "continuously" means every evaluation saw it. A reading that
    /// could not be made (<paramref name="inErrorNow"/> null) RESETS the map: no evidence is not
    /// evidence of a wedge. Pure.
    /// </summary>
    public static ImmutableDictionary<string, DateTimeOffset> FoldCompileErrors(
        ImmutableDictionary<string, DateTimeOffset> since, IReadOnlyCollection<string>? inErrorNow, DateTimeOffset now) =>
        inErrorNow is null
            ? ImmutableDictionary<string, DateTimeOffset>.Empty
            : inErrorNow.Distinct(StringComparer.Ordinal)
                .ToImmutableDictionary(p => p, p => since.TryGetValue(p, out var first) ? first : now, StringComparer.Ordinal);

    /// <summary>
    /// The rate limit: whether a wedge <paramref name="verdict"/> may file a self-reboot now, given the
    /// reboots already on record, and if not, why (the alarm's sentence). Refuses when a reboot is
    /// still in progress, when a self-reboot was taken within <see cref="InstanceRebootOptions.WatchdogMinInterval"/>,
    /// or when the SAME evidence already survived a self-reboot within <see cref="InstanceRebootOptions.WatchdogRepeatWindow"/>
    /// (a reboot does not clear it — a person must act). Pure.
    /// </summary>
    public static (bool Fire, string? Why) Admit(
        WedgeVerdict verdict, IReadOnlyCollection<RebootRecord> previous, DateTimeOffset now, InstanceRebootOptions options)
    {
        if (previous.FirstOrDefault(r => !r.Terminal) is { } open)
            return (false, $"a reboot is already in progress ({open.Path}, {open.Trigger}, requested {open.RequestedAt:u})");
        var selfReboots = previous.Where(r => r.Trigger == InstanceRebootTrigger.Watchdog).OrderByDescending(r => r.RequestedAt).ToImmutableList();
        if (selfReboots.FirstOrDefault(r => now - r.RequestedAt < options.WatchdogMinInterval) is { } recent)
            return (false, $"rate-limited: a self-reboot was taken at {recent.RequestedAt:u} ({recent.Path}) — at most one per "
                           + $"{options.WatchdogMinInterval.TotalHours:0.#} h; a person must act (reboot_instance or the Reboot button)");
        if (selfReboots.FirstOrDefault(r => r.Fingerprint == verdict.Fingerprint && now - r.RequestedAt < options.WatchdogRepeatWindow) is { } same)
            return (false, $"the same evidence survived the self-reboot at {same.RequestedAt:u} ({same.Path}) — a reboot does not "
                           + "clear it; a person must act");
        return (true, null);
    }
}
