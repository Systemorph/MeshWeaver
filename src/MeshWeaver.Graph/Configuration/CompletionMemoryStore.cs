using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Per-user completion acceptance memory — loaded from and saved to
/// <c>{userId}/_Settings/Completions</c>, the same per-user settings-node shape the notification
/// preferences use.
///
/// <para><b>Write discipline.</b> Acceptances arrive one keystroke apart; a node write per
/// acceptance is exactly the write storm this codebase has been burned by. So writes are
/// COALESCED: each acceptance updates memory in-process and nudges a per-user throttle, and only
/// a quiet window later does one write persist whatever the memory has become. A user who accepts
/// twenty completions in a burst costs one write.</para>
///
/// <para><b>Never on the critical path.</b> Loading is a one-shot, empty-on-absent query behind a
/// timeout; a user whose memory has not loaded (or cannot) simply gets no preselection. Nothing
/// here can fail a completion request.</para>
///
/// <para><b>🚨 An unread history is NEVER an empty one.</b> This store persists by REPLACEMENT —
/// one node whose Content is the whole memory — so anything it saves that is not grounded in a
/// completed read of the stored node deletes whatever it did not read. A load that reached no
/// verdict (timed out, faulted, no storage yet) therefore must not become a cached memory and must
/// not authorise a save; the two invariants below are what keep that impossible:</para>
/// <list type="bullet">
///   <item><description><b>Indeterminate loads cache nothing.</b> The load marker is released so
///     the viewer's NEXT completion request re-resolves through the same chain the success path
///     takes — user-driven re-resolution, never a timer or a retry loop.</description></item>
///   <item><description><b>Saves refuse an ungrounded memory</b> (<see cref="MemoryEntry.Loaded"/>).
///     Defence in depth: even if a future edit reintroduced a cached fragment, the write cannot
///     replace the user's stored history with it.</description></item>
/// </list>
/// </summary>
internal sealed class CompletionMemoryStore : IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long the query fold must be quiet before its state counts as the answer.</summary>
    private static readonly TimeSpan LoadQuietWindow = TimeSpan.FromMilliseconds(500);

    private readonly ILogger logger;
    private readonly Func<AccessService?> accessService;
    private readonly Func<JsonSerializerOptions> serializerOptions;
    private readonly Func<string, IObservable<ImmutableList<MeshNode>>> loadNodes;
    private readonly Func<MeshNode, IObservable<MeshNode>> saveNode;
    private readonly IScheduler scheduler;

    private readonly ConcurrentDictionary<string, MemoryEntry> memories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loading = new(StringComparer.Ordinal);
    private readonly Subject<string> dirty = new();
    private readonly IDisposable saveSubscription;
    private int disposed;

    /// <summary>DI constructor: reads and writes the viewer's settings node through the mesh.</summary>
    public CompletionMemoryStore(IMessageHub hub, ILogger logger)
        : this(
            logger,
            () => hub.ServiceProvider.GetService<AccessService>(),
            () => hub.JsonSerializerOptions,
            viewer => QueryStored(hub, viewer),
            node => Persist(hub, node),
            DefaultScheduler.Instance)
    {
    }

    /// <summary>
    /// Seam constructor (unit tests via InternalsVisibleTo): inject the load source, the save sink
    /// and the time source directly — no hub, no mesh service required. The scheduler is what lets
    /// the 15 s load timeout and the 10 s save debounce be exercised in virtual time.
    /// </summary>
    internal CompletionMemoryStore(
        ILogger logger,
        Func<AccessService?> accessService,
        Func<JsonSerializerOptions> serializerOptions,
        Func<string, IObservable<ImmutableList<MeshNode>>> loadNodes,
        Func<MeshNode, IObservable<MeshNode>> saveNode,
        IScheduler scheduler)
    {
        this.logger = logger;
        this.accessService = accessService;
        this.serializerOptions = serializerOptions;
        this.loadNodes = loadNodes;
        this.saveNode = saveNode;
        this.scheduler = scheduler;
        // One save per user per quiet window — the coalescing that keeps acceptance recording
        // from becoming a write storm.
        saveSubscription = dirty
            .GroupBy(viewer => viewer, StringComparer.Ordinal)
            .SelectMany(group => group.Throttle(SaveDebounce, scheduler))
            .Subscribe(Save, ex => logger.LogDebug(ex, "Completion memory save pipeline faulted"));
    }

    /// <summary>The signed-in viewer, or null when there is nobody to remember for.</summary>
    public string? Viewer()
    {
        var access = accessService();
        foreach (var candidate in new[] { access?.Context?.ObjectId, access?.CircuitContext?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !string.Equals(candidate, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }

    /// <summary>
    /// The viewer's memory as currently known in-process, kicking off a load when no load has yet
    /// reached a verdict. Returns empty (never blocks) until that load lands.
    ///
    /// <para>This is also the RE-RESOLUTION point: a load that reached no verdict left nothing
    /// cached, so the viewer's next completion request starts a fresh one. No timer, no retry
    /// loop — the user's own next keystroke is the trigger.</para>
    /// </summary>
    public CompletionMemory For(string viewer)
    {
        memories.TryGetValue(viewer, out var entry);
        if (entry is not { Loaded: true })
            BeginLoad(viewer);
        return entry?.Memory ?? new CompletionMemory();
    }

    /// <summary>Records an acceptance and schedules the coalesced save. Never throws.</summary>
    public void Record(string viewer, string? prefix, string label, int kind)
    {
        try
        {
            memories.AddOrUpdate(
                viewer,
                _ => new MemoryEntry(new CompletionMemory().Record(prefix, label, kind), Loaded: false),
                (_, existing) => existing with { Memory = existing.Memory.Record(prefix, label, kind) });
            // An acceptance is also a reason to resolve the stored history: until a load reaches a
            // verdict this acceptance is unsaveable (Save refuses an ungrounded memory).
            BeginLoad(viewer);
            MarkDirty(viewer);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Recording completion acceptance failed for {Viewer}", viewer);
        }
    }

    /// <summary>The per-user settings node holding the memory.</summary>
    public static string PathFor(string viewer) => $"{viewer}/_Settings/Completions";

    private void BeginLoad(string viewer)
    {
        if (!loading.TryAdd(viewer, 0))
            return;

        Observable.Defer(() => loadNodes(viewer))
            .Throttle(LoadQuietWindow, scheduler)
            .Take(1)
            .Timeout(LoadTimeout, scheduler)
            .Select(nodes => LoadOutcome.Reached(
                nodes.FirstOrDefault().ContentAs<CompletionMemory>(serializerOptions(), logger)))
            // 🚨 A read that reached NO VERDICT is not the same answer as one that found nothing.
            // Folding both into "empty" made a transient stall (busy mesh hub, slow storage, a
            // partition mid-provision) look like "this user has no acceptance history" — and
            // because that answer was CACHED, the next acceptance saved it back, replacing the
            // user's real history with a single entry. Nothing errored; the history was just gone.
            // Keep the two apart, exactly as the NodeType registration probe does (#1085).
            .Catch<LoadOutcome, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "Completion memory load for {Viewer} reached no verdict ({ExceptionType}) — treating it "
                    + "as INDETERMINATE, not empty. Nothing is cached and nothing is saved for this viewer "
                    + "until a load succeeds; the next completion request re-resolves.",
                    viewer, ex.GetType().Name);
                return Observable.Return(LoadOutcome.Indeterminate);
            })
            .Subscribe(
                outcome => Apply(viewer, outcome),
                ex =>
                {
                    // Only reachable if Apply itself threw — the Catch above owns every upstream
                    // fault. Release the marker so the viewer is not stuck without a memory.
                    loading.TryRemove(viewer, out _);
                    logger.LogDebug(ex, "Completion memory load failed for {Viewer}", viewer);
                });
    }

    /// <summary>
    /// Folds one load verdict into the in-process memory.
    ///
    /// <para>An INDETERMINATE verdict leaves the cache untouched and releases the load marker: the
    /// viewer keeps whatever unsaveable acceptances they have accrued, and the next
    /// <see cref="For"/> re-resolves.</para>
    ///
    /// <para>A REACHED verdict grounds the memory in stored history. Acceptances recorded while the
    /// load was in flight are REPLAYED on top of what was read rather than replacing it — the old
    /// <c>TryAdd</c> here silently discarded the loaded history whenever an acceptance had beaten
    /// the load home, which is the same replacement-loss the timeout caused.</para>
    /// </summary>
    private void Apply(string viewer, LoadOutcome outcome)
    {
        if (!outcome.IsReached)
        {
            loading.TryRemove(viewer, out _);
            return;
        }

        var stored = outcome.Stored ?? new CompletionMemory();
        var grounded = memories.AddOrUpdate(
            viewer,
            _ => new MemoryEntry(stored, Loaded: true),
            (_, existing) => existing.Loaded
                ? existing
                : new MemoryEntry(Replay(stored, existing.Memory), Loaded: true));

        // Acceptances made before the verdict landed were refused by the save guard. Now that the
        // memory carries the stored history, let the coalescer persist the merged result.
        if (!ReferenceEquals(grounded.Memory, stored))
            MarkDirty(viewer);
    }

    /// <summary>
    /// Replays in-flight acceptances onto the loaded history, oldest first, through
    /// <see cref="CompletionMemory.Record"/> so its dedup and bound apply. Returns
    /// <paramref name="stored"/> itself when there was nothing pending.
    /// </summary>
    private static CompletionMemory Replay(CompletionMemory stored, CompletionMemory pending) =>
        pending.Entries
            .OrderBy(e => e.Touch)
            .Aggregate(stored, (acc, e) => acc.Record(e.Prefix, e.Label, e.Kind));

    private void Save(string viewer)
    {
        if (!memories.TryGetValue(viewer, out var entry))
            return;

        // 🚨 Defence in depth on a DATA-LOSS path. This save REPLACES the node's whole Content, so
        // a memory that is not grounded in a completed read is missing everything that read would
        // have returned — writing it would substitute an in-process fragment for the user's
        // acceptance history. Skip; a later successful load merges and re-arms the save.
        if (!entry.Loaded)
        {
            logger.LogDebug(
                "Skipping completion memory save for {Viewer} — no load has reached a verdict, so this "
                + "memory would overwrite stored history with an in-process fragment", viewer);
            return;
        }

        var path = PathFor(viewer);
        var node = MeshNode.FromPath(path) with
        {
            Name = "Completion memory",
            NodeType = CompletionMemoryNodeType.NodeType,
            MainNode = viewer,
            State = MeshNodeState.Active,
            Content = entry.Memory,
        };
        Observable.Defer(() => saveNode(node))
            .Take(1)
            .Subscribe(
                _ => { },
                ex => logger.LogDebug(ex, "Completion memory save failed for {Viewer}", viewer));
    }

    /// <summary>
    /// Nudges the save coalescer, tolerating the store being disposed underneath an in-flight
    /// load — that is teardown, not a fault to hide.
    /// </summary>
    private void MarkDirty(string viewer)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        try
        {
            dirty.OnNext(viewer);
        }
        catch (ObjectDisposedException)
        {
            // Raced Dispose: there is no coalescer left to nudge, and nothing to persist into.
        }
    }

    /// <summary>
    /// The viewer's settings node as an empty-on-absent query fold — never a point read of a
    /// maybe-absent path (that is the probe this mesh fast-fails and logs as a defect).
    /// A missing <see cref="IMeshService"/> is an UNAVAILABLE read, not an empty one: it faults so
    /// the chain classifies it INDETERMINATE and no save can be grounded on it.
    /// </summary>
    private static IObservable<ImmutableList<MeshNode>> QueryStored(IMessageHub hub, string viewer)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Throw<ImmutableList<MeshNode>>(new InvalidOperationException(
                "No IMeshService is registered — the completion memory cannot be read."));

        return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{PathFor(viewer)}"))
            .Scan(ImmutableList<MeshNode>.Empty, (list, change) => change.ChangeType switch
            {
                QueryChangeType.Removed => ImmutableList<MeshNode>.Empty,
                _ => change.Items.ToImmutableList(),
            });
    }

    private static IObservable<MeshNode> Persist(IMessageHub hub, MeshNode node)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        return mesh is null
            ? Observable.Throw<MeshNode>(new InvalidOperationException(
                "No IMeshService is registered — the completion memory cannot be saved."))
            : mesh.CreateOrUpdateNode(node);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        saveSubscription.Dispose();
        dirty.Dispose();
    }

    /// <summary>
    /// The viewer's in-process memory plus the one fact that decides whether it may be persisted.
    /// </summary>
    /// <param name="Memory">The acceptance history as currently known in this process.</param>
    /// <param name="Loaded">
    /// True only when a load REACHED A VERDICT (found the node, or established that there is
    /// nothing readable at that path) and this memory is built on top of it. False means the
    /// entries here are whatever this process happened to observe — usable for preselection,
    /// never safe to write back, because a write replaces the stored node wholesale.
    /// </param>
    private sealed record MemoryEntry(CompletionMemory Memory, bool Loaded);

    /// <summary>
    /// What ONE bounded read of the viewer's settings node established — the seam that keeps "we
    /// could not find out" apart from "we found out, and there is nothing stored".
    /// </summary>
    /// <param name="IsReached">
    /// True when the read completed. <paramref name="Stored"/> is then the history, or null for a
    /// DEFINITIVE "nothing stored here". False means no verdict at all.
    /// </param>
    /// <param name="Stored">The stored history when one was read; null otherwise.</param>
    private readonly record struct LoadOutcome(bool IsReached, CompletionMemory? Stored)
    {
        /// <summary>The read completed; <paramref name="stored"/> may be null for a definitive absence.</summary>
        public static LoadOutcome Reached(CompletionMemory? stored) => new(true, stored);

        /// <summary>The read reached no verdict — cache nothing, save nothing, re-resolve later.</summary>
        public static LoadOutcome Indeterminate { get; } = new(false, null);
    }
}
