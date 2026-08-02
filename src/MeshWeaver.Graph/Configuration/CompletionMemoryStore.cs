using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// </summary>
internal sealed class CompletionMemoryStore : IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    private readonly IMessageHub hub;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, CompletionMemory> memories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loading = new(StringComparer.Ordinal);
    private readonly Subject<string> dirty = new();
    private readonly IDisposable saveSubscription;

    public CompletionMemoryStore(IMessageHub hub, ILogger logger)
    {
        this.hub = hub;
        this.logger = logger;
        // One save per user per quiet window — the coalescing that keeps acceptance recording
        // from becoming a write storm.
        saveSubscription = dirty
            .GroupBy(viewer => viewer, StringComparer.Ordinal)
            .SelectMany(group => group.Throttle(SaveDebounce))
            .Subscribe(Save, ex => logger.LogDebug(ex, "Completion memory save pipeline faulted"));
    }

    /// <summary>The signed-in viewer, or null when there is nobody to remember for.</summary>
    public string? Viewer()
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        foreach (var candidate in new[] { access?.Context?.ObjectId, access?.CircuitContext?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !string.Equals(candidate, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }

    /// <summary>
    /// The viewer's memory as currently known in-process, kicking off a one-time load when it has
    /// not been read yet. Returns empty (never blocks) until that load lands.
    /// </summary>
    public CompletionMemory For(string viewer)
    {
        if (memories.TryGetValue(viewer, out var memory))
            return memory;
        BeginLoad(viewer);
        return new CompletionMemory();
    }

    /// <summary>Records an acceptance and schedules the coalesced save. Never throws.</summary>
    public void Record(string viewer, string? prefix, string label, int kind)
    {
        try
        {
            memories.AddOrUpdate(
                viewer,
                _ => new CompletionMemory().Record(prefix, label, kind),
                (_, existing) => existing.Record(prefix, label, kind));
            dirty.OnNext(viewer);
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

        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
        {
            memories.TryAdd(viewer, new CompletionMemory());
            return;
        }

        var path = PathFor(viewer);
        // Empty-on-absent query — never a point read of a maybe-absent path (that is the probe
        // this mesh fast-fails and logs as a defect).
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
            .Scan(ImmutableList<MeshNode>.Empty, (list, change) => change.ChangeType switch
            {
                QueryChangeType.Removed => ImmutableList<MeshNode>.Empty,
                _ => change.Items.ToImmutableList(),
            })
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Take(1)
            .Timeout(LoadTimeout)
            .Catch(Observable.Return(ImmutableList<MeshNode>.Empty))
            .Subscribe(
                nodes =>
                {
                    var stored = nodes.FirstOrDefault()
                        ?.ContentAs<CompletionMemory>(hub.JsonSerializerOptions);
                    // A load must never clobber acceptances recorded while it was in flight.
                    memories.TryAdd(viewer, stored ?? new CompletionMemory());
                },
                ex => logger.LogDebug(ex, "Completion memory load failed for {Viewer}", viewer));
    }

    private void Save(string viewer)
    {
        if (!memories.TryGetValue(viewer, out var memory))
            return;
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return;

        var path = PathFor(viewer);
        var node = MeshNode.FromPath(path) with
        {
            Name = "Completion memory",
            NodeType = CompletionMemoryNodeType.NodeType,
            MainNode = viewer,
            State = MeshNodeState.Active,
            Content = memory,
        };
        mesh.CreateOrUpdateNode(node)
            .Take(1)
            .Subscribe(
                _ => { },
                ex => logger.LogDebug(ex, "Completion memory save failed for {Viewer}", viewer));
    }

    public void Dispose()
    {
        saveSubscription.Dispose();
        dirty.Dispose();
    }
}
