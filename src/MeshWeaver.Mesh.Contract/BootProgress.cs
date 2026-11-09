using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The boot phases <see cref="BootProgress"/> reports. An OPEN vocabulary (policy
/// <c>open-vocabulary-string-constants</c>): a reader shows an id it does not know by its
/// <see cref="BootPhase.Title"/> rather than refusing it.
/// </summary>
public static class BootPhaseIds
{
    /// <summary>The NodeType bake the default install waits for before it starts.</summary>
    public const string Bake = "bake";

    /// <summary>Listing the selected packages from the configured sources (the module set).</summary>
    public const string Resolve = "resolve";

    /// <summary>Downloading and landing the compiled modules of the packages not installed yet.</summary>
    public const string Modules = "modules";

    /// <summary>Installing each package's nodes.</summary>
    public const string Content = "content";

    /// <summary>The restart that activates newly landed modules (restart-as-activation).</summary>
    public const string Activate = "activate";
}

/// <summary>The states of a <see cref="BootPhase"/>. Open vocabulary.</summary>
public static class BootStepStates
{
    /// <summary>Not started.</summary>
    public const string Pending = "pending";
    /// <summary>In progress.</summary>
    public const string Running = "running";
    /// <summary>Finished.</summary>
    public const string Done = "done";
    /// <summary>Finished with an error that is named in <see cref="BootPhase.Detail"/>.</summary>
    public const string Failed = "failed";
    /// <summary>Nothing to do in this boot.</summary>
    public const string Skipped = "skipped";
}

/// <summary>The states of one <see cref="BootModuleStep"/>. Open vocabulary.</summary>
public static class BootModuleStates
{
    /// <summary>Waiting for a landing slot.</summary>
    public const string Queued = "queued";
    /// <summary>Downloading or landing now.</summary>
    public const string Running = "running";
    /// <summary>The bytes landed; the module loads at the next process start.</summary>
    public const string Landed = "landed";
    /// <summary>Nothing to land: the module on the volume already is the served one.</summary>
    public const string Current = "current";
    /// <summary>Not decided here: the package is already installed and keeps its own funnel.</summary>
    public const string Skipped = "skipped";
    /// <summary>Did not land; <see cref="BootModuleStep.Error"/> names why.</summary>
    public const string Failed = "failed";
}

/// <summary>One phase of a boot, as <see cref="BootProgress"/> recorded it.</summary>
/// <param name="Id">One of <see cref="BootPhaseIds"/> (or a module's own id).</param>
/// <param name="Title">What a person reads, in English; a reader may map <paramref name="Id"/> to its own wording.</param>
public sealed record BootPhase(string Id, string Title)
{
    /// <summary>One of <see cref="BootStepStates"/>.</summary>
    public string State { get; init; } = BootStepStates.Pending;
    /// <summary>When the phase started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>When the phase ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>Units finished so far, for a counted phase.</summary>
    public int? Done { get; init; }
    /// <summary>Units in the phase, for a counted phase.</summary>
    public int? Total { get; init; }
    /// <summary>Units that failed so far.</summary>
    public int? Failed { get; init; }
    /// <summary>How many units run at once.</summary>
    public int? Concurrency { get; init; }
    /// <summary>A sentence on what the phase is doing or what went wrong.</summary>
    public string? Detail { get; init; }
}

/// <summary>One module of the module phase.</summary>
/// <param name="Package">The package id.</param>
/// <param name="Module">The module's entry-assembly name.</param>
public sealed record BootModuleStep(string Package, string Module)
{
    /// <summary>The version the registry served, once known.</summary>
    public string? Version { get; init; }
    /// <summary>One of <see cref="BootModuleStates"/>.</summary>
    public string State { get; init; } = BootModuleStates.Queued;
    /// <summary>The dependency level the module lands in (0 = no dependency among the selection).</summary>
    public int Level { get; init; }
    /// <summary>When its slot started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>When it was decided.</summary>
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>The bundle's size, once downloaded.</summary>
    public long? Bytes { get; init; }
    /// <summary>How long the download took, request to last byte.</summary>
    public double? DownloadSeconds { get; init; }
    /// <summary>Why it did not land.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// What <see cref="BootProgress"/> holds: the facts of THIS process's boot, plus the boot of the
/// same host before it (the process a restart-as-activation replaced), so a reader can say why the
/// portal restarted and what that boot spent its time on.
/// </summary>
public sealed record BootProgressSnapshot
{
    /// <summary>The contract version of this document.</summary>
    public int Schema { get; init; } = 1;
    /// <summary>The host (pod) name — a restart keeps it, a roll does not.</summary>
    public string? Host { get; init; }
    /// <summary>When this process started.</summary>
    public DateTimeOffset ProcessStartedAt { get; init; }
    /// <summary>The phases, in boot order.</summary>
    public ImmutableList<BootPhase> Phases { get; init; } = [];
    /// <summary>The module phase's modules, in landing order.</summary>
    public ImmutableList<BootModuleStep> Modules { get; init; } = [];
    /// <summary>Every error the boot named, newest last (bounded).</summary>
    public ImmutableList<string> Errors { get; init; } = [];
    /// <summary>The previous boot of the same host, or null.</summary>
    public BootProgressSnapshot? PreviousBoot { get; init; }

    /// <summary>The phase with <paramref name="id"/>, or null.</summary>
    public BootPhase? Phase(string id) => Phases.FirstOrDefault(p => p.Id == id);
}

/// <summary>One readiness check as the report shows it.</summary>
/// <param name="Name">The check's registration name.</param>
/// <param name="Status">Healthy, Degraded or Unhealthy.</param>
/// <param name="Description">The check's own words.</param>
public sealed record BootCheck(string Name, string Status, string? Description);

/// <summary>A phase as the report shows it: the recorded facts plus what is DERIVED at read time.</summary>
public sealed record BootPhaseReport
{
    /// <summary>The phase id.</summary>
    public required string Id { get; init; }
    /// <summary>The phase title.</summary>
    public required string Title { get; init; }
    /// <summary>The phase state.</summary>
    public required string State { get; init; }
    /// <summary>When it started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>When it ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>Seconds spent — to its end, or to now while it runs.</summary>
    public double? Seconds { get; init; }
    /// <summary>Units done.</summary>
    public int? Done { get; init; }
    /// <summary>Units in all.</summary>
    public int? Total { get; init; }
    /// <summary>Units failed.</summary>
    public int? Failed { get; init; }
    /// <summary>Units at once.</summary>
    public int? Concurrency { get; init; }
    /// <summary>Seconds left at the measured rate, or null when no rate is measured yet.</summary>
    public double? EtaSeconds { get; init; }
    /// <summary>The phase's sentence.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// 🚨 THE boot progress document — served at <c>/_boot/progress</c> by the portal, read by the
/// boot page while the portal is not ready and by <c>memex-local</c> in the terminal. ONE renderer
/// (<see cref="From"/>) so the two readers cannot disagree about a fact or an estimate.
/// </summary>
public sealed record BootProgressReport
{
    /// <summary>The contract version.</summary>
    public int Schema { get; init; } = 1;
    /// <summary>When the report was made.</summary>
    public DateTimeOffset Now { get; init; }
    /// <summary>When this process started.</summary>
    public DateTimeOffset ProcessStartedAt { get; init; }
    /// <summary>Seconds since this process started.</summary>
    public double UptimeSeconds { get; init; }
    /// <summary>Whether the portal answers its readiness probe.</summary>
    public bool Ready { get; init; }
    /// <summary>The running phase's id, <c>ready</c>, or <c>starting</c> before any phase ran.</summary>
    public required string Phase { get; init; }
    /// <summary>One line: what is happening now.</summary>
    public required string Headline { get; init; }
    /// <summary>Seconds left in the running phase at its measured rate, or null.</summary>
    public double? EtaSeconds { get; init; }
    /// <summary>Every phase, derived values included.</summary>
    public ImmutableList<BootPhaseReport> Phases { get; init; } = [];
    /// <summary>The module phase's modules (dropped once ready: they are boot detail, not state).</summary>
    public ImmutableList<BootModuleStep> Modules { get; init; } = [];
    /// <summary>The readiness checks that are not Healthy, with their own words.</summary>
    public ImmutableList<BootCheck> Checks { get; init; } = [];
    /// <summary>Every error named during this boot.</summary>
    public ImmutableList<string> Errors { get; init; } = [];
    /// <summary>The previous boot of this host, summarised (its phases and counts), or null.</summary>
    public BootProgressReport? PreviousBoot { get; init; }

    /// <summary>The serializer options the endpoint and every test use: camelCase, nulls omitted.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Renders <paramref name="snapshot"/> at <paramref name="now"/>. Pure.
    ///
    /// <para>The ETA of a counted phase is <c>(total − done) × elapsed ÷ done</c> — the phase's
    /// OWN measured throughput, so it already accounts for how many units run at once, and it is
    /// null until one unit has finished (a guess before the first measurement is not an ETA).</para>
    /// </summary>
    /// <param name="snapshot">The recorded facts.</param>
    /// <param name="now">The reading's instant.</param>
    /// <param name="ready">Whether the readiness probe answers healthy.</param>
    /// <param name="checks">The readiness checks that are not Healthy.</param>
    public static BootProgressReport From(
        BootProgressSnapshot snapshot, DateTimeOffset now, bool ready, IReadOnlyList<BootCheck>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var phases = snapshot.Phases.Select(p => PhaseAt(p, now)).ToImmutableList();
        var running = phases.LastOrDefault(p => p.State == BootStepStates.Running);
        var phase = ready ? "ready" : running?.Id ?? phases.LastOrDefault(p => p.State != BootStepStates.Pending)?.Id ?? "starting";
        return new BootProgressReport
        {
            Schema = snapshot.Schema,
            Now = now,
            ProcessStartedAt = snapshot.ProcessStartedAt,
            UptimeSeconds = Seconds(snapshot.ProcessStartedAt, now),
            Ready = ready,
            Phase = phase,
            Headline = HeadlineOf(snapshot, running, ready, checks),
            EtaSeconds = ready ? null : running?.EtaSeconds,
            Phases = phases,
            Modules = ready ? [] : snapshot.Modules,
            Checks = (checks ?? []).ToImmutableList(),
            Errors = snapshot.Errors,
            PreviousBoot = snapshot.PreviousBoot is { } previous
                ? From(previous with { PreviousBoot = null }, Last(previous) ?? previous.ProcessStartedAt, ready: false) with
                {
                    Modules = [],
                    Headline = PreviousHeadline(previous),
                }
                : null,
        };
    }

    private static BootPhaseReport PhaseAt(BootPhase p, DateTimeOffset now)
    {
        var end = p.EndedAt ?? (p.State == BootStepStates.Running ? now : (DateTimeOffset?)null);
        var seconds = p.StartedAt is { } s && end is { } e ? Seconds(s, e) : (double?)null;
        double? eta = null;
        if (p.State == BootStepStates.Running && p.Done is > 0 && p.Total is { } total && seconds is > 0)
            eta = Math.Round(Math.Max(0, total - p.Done.Value) * seconds.Value / p.Done.Value, 1);
        return new BootPhaseReport
        {
            Id = p.Id, Title = p.Title, State = p.State, StartedAt = p.StartedAt, EndedAt = p.EndedAt,
            Seconds = seconds, Done = p.Done, Total = p.Total, Failed = p.Failed,
            Concurrency = p.Concurrency, EtaSeconds = eta, Detail = p.Detail,
        };
    }

    private static string HeadlineOf(
        BootProgressSnapshot snapshot, BootPhaseReport? running, bool ready, IReadOnlyList<BootCheck>? checks)
    {
        if (ready)
            return "Ready";
        if (running is null)
        {
            var waiting = (checks ?? []).Select(c => c.Name).ToList();
            return waiting.Count > 0
                ? $"Waiting for readiness: {string.Join(", ", waiting)}"
                : "Starting";
        }
        var counted = running.Total is { } total ? $" — {running.Done ?? 0} of {total}" : "";
        if (running.Id == BootPhaseIds.Modules)
        {
            var now = snapshot.Modules.Where(m => m.State == BootModuleStates.Running)
                .Select(m => m.Version is { } v ? $"{m.Package} {v}" : m.Package).ToList();
            if (now.Count > 0)
                counted += $" ({string.Join(", ", now)})";
        }
        return running.Title + counted;
    }

    private static string PreviousHeadline(BootProgressSnapshot previous)
    {
        var landed = previous.Modules.Count(m => m.State == BootModuleStates.Landed);
        var activate = previous.Phase(BootPhaseIds.Activate);
        return activate is { State: BootStepStates.Running or BootStepStates.Done }
            ? $"Restarted to activate {landed} landed module(s)"
            : "Previous start of this portal";
    }

    private static DateTimeOffset? Last(BootProgressSnapshot s) =>
        s.Phases.Select(p => p.EndedAt ?? p.StartedAt).Where(t => t is not null).Max();

    private static double Seconds(DateTimeOffset from, DateTimeOffset to) =>
        Math.Round(Math.Max(0, (to - from).TotalSeconds), 1);
}

/// <summary>
/// 🚨 <b>The ONE recorder of a portal's boot progress</b> — what the person waiting on a fresh
/// instance sees, on the boot page and in the <c>memex-local</c> terminal alike. The maintainer,
/// 2026-11-09, watching a fresh Mac instance land its registry modules one at a time behind a
/// spinner: <i>"can we bring better progress bar?"</i>.
///
/// <para><b>Facts, not a second implementation.</b> The code that does the work records it here —
/// the default install's phases (<c>MeshWeaver.PluginCatalog.InstanceAutoRegistrationService</c>), each module's
/// download (<c>MeshWeaver.PluginCatalog.PluginBundleClient</c>) — and every reader renders the same snapshot through
/// <see cref="BootProgressReport.From"/>. Nothing parses a log line.</para>
///
/// <para><b>Survives the activation restart.</b> A fresh instance lands its modules and then
/// restarts to load them; the snapshot is written (sampled, best effort) to
/// <c>{Modules:Root}/boot/{host}.json</c> on the file-system pool, and the next process of the same
/// host reads it back as <see cref="BootProgressSnapshot.PreviousBoot"/> — so the page can say
/// "restarted to activate 48 modules" instead of starting its story over. Without a configured
/// module root nothing is written (the default root is the read-only app directory).</para>
///
/// <para>Lock-free: the state is an immutable snapshot swapped with
/// <see cref="ImmutableInterlocked.Update{T}(ref T, Func{T, T})"/>; no gate, no lock.</para>
/// </summary>
public sealed class BootProgress : IDisposable
{
    /// <summary>The number of errors kept, newest last.</summary>
    public const int MaxErrors = 50;

    /// <summary>How often the snapshot is written to the volume while it changes.</summary>
    public static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(2);

    private BootProgressSnapshot state;
    private readonly ISubject<Unit> changed = Subject.Synchronize(new Subject<Unit>());
    private readonly IDisposable persistence;
    private readonly TimeProvider clock;
    private readonly ILogger? logger;

    /// <summary>A recorder that persists nothing (tests, and a host without a module root).</summary>
    public BootProgress() : this(directory: null, pool: null, logger: null) { }

    /// <summary>The recorder.</summary>
    /// <param name="directory">Where the snapshot is persisted, or null to persist nothing.</param>
    /// <param name="pool">The file-system pool the writes and the previous-boot read run on.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="clock">The clock; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="host">The host name; <see cref="Environment.MachineName"/> by default.</param>
    /// <param name="processStartedAt">This process's start; the OS's answer by default.</param>
    public BootProgress(
        string? directory, IIoPool? pool, ILogger? logger, TimeProvider? clock = null, string? host = null,
        DateTimeOffset? processStartedAt = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger;
        var name = host ?? Environment.MachineName;
        state = new BootProgressSnapshot
        {
            Host = name,
            ProcessStartedAt = processStartedAt ?? ProcessStart(this.clock),
        };
        if (directory is null || pool is null)
        {
            persistence = System.Reactive.Disposables.Disposable.Empty;
            return;
        }
        var file = Path.Combine(directory, FileName(name));
        var previous = pool.InvokeBlocking(ct => ReadPrevious(file, ct))
            .Catch((Exception ex) =>
            {
                logger?.LogInformation("[BootProgress] the previous boot record {File} could not be read: {Cause}", file, ex.Message);
                return Observable.Return<BootProgressSnapshot?>(null);
            })
            .Do(prev =>
            {
                if (prev is not null)
                    Update(s => s with { PreviousBoot = prev with { PreviousBoot = null } });
            });
        // Sampled, one write at a time (Concat), every write absorbed: a volume that refuses a write
        // costs the next reader the restart story, never the boot.
        persistence = previous.Select(_ => Unit.Default)
            // StartWith: a change made while the previous record was being read is not lost —
            // the first sampled write carries whatever the snapshot holds by then.
            .Concat(changed.StartWith(Unit.Default).Sample(PersistInterval)
                .Select(_ => pool.InvokeBlocking(ct => Write(directory, file, Current, ct))
                    .Catch((Exception ex) =>
                    {
                        logger?.LogInformation("[BootProgress] could not write {File}: {Cause}", file, ex.Message);
                        return Observable.Return(Unit.Default);
                    }))
                .Concat())
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "[BootProgress] persistence stopped"));
    }

    /// <summary>The current snapshot.</summary>
    public BootProgressSnapshot Current => Volatile.Read(ref state);

    /// <summary>Emits on every change (not the value — read <see cref="Current"/>).</summary>
    public IObservable<Unit> Changed => changed.AsObservable();

    /// <summary>The clock this recorder stamps with.</summary>
    public TimeProvider Clock => clock;

    /// <summary>Starts (or restarts) a phase; the phases after it in a new pass are reset.</summary>
    public void PhaseStarted(string id, string title, string? detail = null, int? total = null, int? concurrency = null) =>
        Update(s => s with
        {
            Phases = Upsert(s.Phases, new BootPhase(id, title)
            {
                State = BootStepStates.Running,
                StartedAt = clock.GetUtcNow(),
                Detail = detail,
                Total = total,
                Done = total is null ? null : 0,
                Failed = total is null ? null : 0,
                Concurrency = concurrency,
            }),
        });

    /// <summary>Counts one unit of a running phase as done (and failed, when <paramref name="failed"/>).</summary>
    public void PhaseStep(string id, bool failed = false) =>
        Update(s => s with
        {
            Phases = s.Phases.Select(p => p.Id != id ? p : p with
            {
                Done = (p.Done ?? 0) + 1,
                Failed = (p.Failed ?? 0) + (failed ? 1 : 0),
            }).ToImmutableList(),
        });

    /// <summary>Ends a phase in <paramref name="stateValue"/> (one of <see cref="BootStepStates"/>).</summary>
    public void PhaseEnded(string id, string stateValue, string? detail = null) =>
        Update(s => s with
        {
            Phases = s.Phases.Any(p => p.Id == id)
                ? s.Phases.Select(p => p.Id != id ? p : p with
                {
                    State = stateValue,
                    EndedAt = clock.GetUtcNow(),
                    Detail = detail ?? p.Detail,
                }).ToImmutableList()
                : s.Phases.Add(new BootPhase(id, id) { State = stateValue, StartedAt = clock.GetUtcNow(), EndedAt = clock.GetUtcNow(), Detail = detail }),
        });

    /// <summary>Lists the module phase's modules, all queued — replacing the last pass's list.</summary>
    public void ModulesPlanned(IEnumerable<BootModuleStep> modules) =>
        Update(s => s with { Modules = modules.ToImmutableList() });

    /// <summary>A module's slot started.</summary>
    public void ModuleStarted(string package) =>
        UpdateModule(package, m => m with { State = BootModuleStates.Running, StartedAt = clock.GetUtcNow() });

    /// <summary>A bundle download finished — recorded on the module of <paramref name="package"/> if it is listed.</summary>
    public void BundleDownloaded(string package, string version, long bytes, TimeSpan elapsed) =>
        UpdateModule(package, m => m with
        {
            Version = m.Version ?? version,
            Bytes = bytes,
            DownloadSeconds = Math.Round(elapsed.TotalSeconds, 1),
        });

    /// <summary>A module was decided.</summary>
    /// <param name="package">Its package.</param>
    /// <param name="stateValue">One of <see cref="BootModuleStates"/>.</param>
    /// <param name="version">The version decided, when known.</param>
    /// <param name="error">Why it did not land.</param>
    public void ModuleEnded(string package, string stateValue, string? version = null, string? error = null)
    {
        UpdateModule(package, m => m with
        {
            State = stateValue,
            Version = version ?? m.Version,
            EndedAt = clock.GetUtcNow(),
            Error = error,
        });
        if (error is not null && stateValue == BootModuleStates.Failed)
            Error($"{package}: {error}");
    }

    /// <summary>Names an error of this boot.</summary>
    public void Error(string message) =>
        Update(s => s with
        {
            Errors = (s.Errors.Count >= MaxErrors ? s.Errors.RemoveAt(0) : s.Errors).Add(message),
        });

    /// <inheritdoc />
    public void Dispose() => persistence.Dispose();

    private void UpdateModule(string package, Func<BootModuleStep, BootModuleStep> change) =>
        Update(s => s.Modules.Any(m => string.Equals(m.Package, package, StringComparison.OrdinalIgnoreCase))
            ? s with
            {
                Modules = s.Modules
                    .Select(m => string.Equals(m.Package, package, StringComparison.OrdinalIgnoreCase) ? change(m) : m)
                    .ToImmutableList(),
            }
            : s);

    private void Update(Func<BootProgressSnapshot, BootProgressSnapshot> change)
    {
        ImmutableInterlocked.Update(ref state, change);
        changed.OnNext(Unit.Default);
    }

    private static ImmutableList<BootPhase> Upsert(ImmutableList<BootPhase> phases, BootPhase phase)
    {
        var index = phases.FindIndex(p => p.Id == phase.Id);
        return index < 0 ? phases.Add(phase) : phases.SetItem(index, phase);
    }

    /// <summary>The snapshot's file name for <paramref name="host"/> — characters a file name cannot carry become '_'. Pure.</summary>
    internal static string FileName(string host) =>
        new string(host.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray()) + ".json";

    private static BootProgressSnapshot? ReadPrevious(string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(file))
            return null;
        return JsonSerializer.Deserialize<BootProgressSnapshot>(File.ReadAllText(file), BootProgressReport.JsonOptions);
    }

    private static Unit Write(string directory, string file, BootProgressSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        // Atomic: a reader (the next boot) sees the old file or the new one, never half of one.
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot with { PreviousBoot = null }, BootProgressReport.JsonOptions));
        File.Move(temp, file, overwrite: true);
        return Unit.Default;
    }

    private static DateTimeOffset ProcessStart(TimeProvider clock)
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return clock.GetUtcNow();
        }
    }
}
