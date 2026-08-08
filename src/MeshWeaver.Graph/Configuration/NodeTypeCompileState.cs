using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The MESH-OWNED compile state of a NodeType, as a value of its own — the exact operational
/// members the framework persists onto <see cref="NodeTypeDefinition"/> today (status,
/// timestamps, assembly pointers, source-version maps, release-request bookkeeping), projected
/// into a record that lives OFF the authored node: on the fixed-id compile-state satellite at
/// <c>{type}/_Activity/compile-state</c> (issue #748, phase 1).
///
/// <para>Why: the NodeType node is repo-authored content, and every compile currently rewrites it —
/// which made GitSync treat bookkeeping churn as server edits, carried stale verdicts into git, and
/// let imports stamp them back (the stale-green class; see <see cref="NodeTypeOperationalContent"/>
/// for the transitional seams). The satellite lives under <c>_Activity</c>, so GitSync export,
/// prune protection and the activity table mapping all apply for free — the same pattern as the
/// import manifest (<c>{partition}/_Activity/import-manifest</c>).</para>
///
/// <para>Phase 1 (this record + <see cref="NodeTypeCompileStateMirror"/>) DUAL-WRITES: the node
/// stays the writers' target and the single source of truth, and the mirror projects every real
/// state change onto the satellite. Phase 2 flips readers to the satellite; phase 3 stops writing
/// the node members entirely and retires the mirror.</para>
///
/// <para>🚨 <b>Constraint phases 2/3 must design around: <see cref="NodeTypeDefinition.CompilationStatus"/>
/// IS the compile lock, and the lock cannot simply follow the state onto the satellite.</b> Compile
/// single-flight is a compare-and-swap — <c>HandleDispatchCompile</c> transitions Pending →
/// Compiling inside the per-NodeType hub's OWN <c>Update</c> and dispatches Roslyn only when THAT
/// lambda made the transition — and it is atomic solely because the NodeType hub owns the node it
/// swaps on, so its action block serialises every contender. The satellite is a DIFFERENT node at a
/// DIFFERENT path, therefore a different owner: <c>MeshNodeStreamHandle.IsOwn</c> is plain path
/// equality with the hub address, so from the NodeType hub the satellite is a REMOTE handle. A
/// remote <c>Update</c> evaluates its lambda against the local mirror and ships the diff, so the
/// guard would be read off a snapshot that is not the owner's state at apply time: two contenders
/// can both conclude "I transitioned" and both dispatch Roslyn, and the merge refusing one write
/// afterwards does not un-dispatch the compile it already started. The trigger pair
/// (<see cref="NodeTypeDefinition.RequestedReleaseAt"/> /
/// <see cref="NodeTypeDefinition.LastReleaseRequestHandledAt"/>) has the same shape.
/// So the control plane moves only ONE of two ways — both single-owner, never a split:
/// (a) the compile watchers move WITH it, onto the satellite's own hub, which then owns the CAS; or
/// (b) the control-plane members stay on the node and only the RESULT members move. Splitting the
/// CAS across two owners is the one design that must not be shipped; the double-compile it admits
/// is precisely the "hub unresponsive after the second compile" wedge class.</para>
///
/// <para>The member set is pinned to <see cref="NodeTypeOperationalContent.MemberNames"/> by test —
/// the satellite carries exactly what the sync seams mask, so the two mechanisms cannot drift.</para>
/// </summary>
public record NodeTypeCompileState
{
    /// <summary>See <see cref="NodeTypeDefinition.CompilationStatus"/>.</summary>
    public CompilationStatus? CompilationStatus { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.CompilationError"/>.</summary>
    public string? CompilationError { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.CompilationDiagnostics"/>.</summary>
    public ImmutableList<DiagnosticInfo>? CompilationDiagnostics { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LastCompileStartedAt"/>.</summary>
    public DateTimeOffset? LastCompileStartedAt { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LastCompileSucceededAt"/>.</summary>
    public DateTimeOffset? LastCompileSucceededAt { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LastCompiledVersion"/>.</summary>
    public long? LastCompiledVersion { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LastCompilationActivityPath"/>.</summary>
    public string? LastCompilationActivityPath { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LatestReleasePath"/>.</summary>
    public string? LatestReleasePath { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.RequestedReleasePath"/>.</summary>
    public string? RequestedReleasePath { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.RequestedReleaseAt"/>.</summary>
    public DateTimeOffset? RequestedReleaseAt { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.RequestedReleaseForce"/>.</summary>
    public bool RequestedReleaseForce { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.RequestedReleaseBy"/>.</summary>
    public string? RequestedReleaseBy { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LastReleaseRequestHandledAt"/>.</summary>
    public DateTimeOffset? LastReleaseRequestHandledAt { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.ReleaseNotes"/>.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LatestAssemblyCollection"/>.</summary>
    public string? LatestAssemblyCollection { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.LatestAssemblyPath"/>.</summary>
    public string? LatestAssemblyPath { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.CompiledSources"/>.</summary>
    public IReadOnlyDictionary<string, long>? CompiledSources { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.CurrentSourceVersions"/>.</summary>
    public IReadOnlyDictionary<string, long>? CurrentSourceVersions { get; init; }

    /// <summary>See <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>.</summary>
    public string? CompiledFrameworkVersion { get; init; }

    /// <summary>The state projected from a NodeType definition — pure. Null in, null out.</summary>
    public static NodeTypeCompileState? FromDefinition(NodeTypeDefinition? definition) =>
        definition is null
            ? null
            : new NodeTypeCompileState
            {
                CompilationStatus = definition.CompilationStatus,
                CompilationError = definition.CompilationError,
                CompilationDiagnostics = definition.CompilationDiagnostics,
                LastCompileStartedAt = definition.LastCompileStartedAt,
                LastCompileSucceededAt = definition.LastCompileSucceededAt,
                LastCompiledVersion = definition.LastCompiledVersion,
                LastCompilationActivityPath = definition.LastCompilationActivityPath,
                LatestReleasePath = definition.LatestReleasePath,
                RequestedReleasePath = definition.RequestedReleasePath,
                RequestedReleaseAt = definition.RequestedReleaseAt,
                RequestedReleaseForce = definition.RequestedReleaseForce,
                RequestedReleaseBy = definition.RequestedReleaseBy,
                LastReleaseRequestHandledAt = definition.LastReleaseRequestHandledAt,
                ReleaseNotes = definition.ReleaseNotes,
                LatestAssemblyCollection = definition.LatestAssemblyCollection,
                LatestAssemblyPath = definition.LatestAssemblyPath,
                CompiledSources = definition.CompiledSources,
                CurrentSourceVersions = definition.CurrentSourceVersions,
                CompiledFrameworkVersion = definition.CompiledFrameworkVersion,
            };

    /// <summary>Whether NO compile machinery has recorded anything yet — a never-compiled,
    /// never-requested type. An empty state is not worth a satellite node.</summary>
    public bool IsEmpty =>
        CompilationStatus is null && CompilationError is null && CompilationDiagnostics is null
        && LastCompileStartedAt is null && LastCompileSucceededAt is null
        && LastCompiledVersion is null && LastCompilationActivityPath is null
        && LatestReleasePath is null && RequestedReleasePath is null
        && RequestedReleaseAt is null && !RequestedReleaseForce && RequestedReleaseBy is null
        && LastReleaseRequestHandledAt is null && ReleaseNotes is null
        && LatestAssemblyCollection is null && LatestAssemblyPath is null
        && CompiledSources is null && CurrentSourceVersions is null
        && CompiledFrameworkVersion is null;
}

/// <summary>
/// Phase-1 dual-write of issue #748: mirrors a NodeType's operational compile state from its own
/// MeshNode onto the fixed-id satellite at <c>{type}/_Activity/compile-state</c> — an Activity
/// node whose <see cref="ActivityLog.ReturnValue"/> carries the <see cref="NodeTypeCompileState"/>
/// (the import-manifest pattern). Installed on the per-NodeType hub beside the compile watchers.
///
/// <para>Write discipline: the mirror seeds itself with the ALREADY-PERSISTED satellite state so a
/// mere hub re-activation writes nothing; it writes only when the projected state actually changes
/// (serialized-form comparison — record equality is reference-based for the dictionary members);
/// writes run under System (the satellite is framework bookkeeping, not user content) and are
/// best-effort — a failed mirror write logs and never disturbs the compile pipeline. No self-feed:
/// the mirror writes a satellite, never the node it observes.</para>
/// </summary>
public static class NodeTypeCompileStateMirror
{
    /// <summary>The fixed satellite node id.</summary>
    public const string StateId = "compile-state";

    /// <summary>The satellite path for a NodeType at <paramref name="nodeTypePath"/>.</summary>
    public static string StatePath(string nodeTypePath) => $"{nodeTypePath}/_Activity/{StateId}";

    /// <summary>
    /// Reads the persisted compile state from the satellite — null when the satellite is ABSENT
    /// ("not mirrored yet"; <c>GetMeshNode</c> itself emits null for a missing node) or its
    /// content unparsable. A timeout or denial PROPAGATES: collapsing those into "no state"
    /// would hand phase-2 readers a silent wrong answer for a mesh stall. Cold; one emission.
    /// </summary>
    public static IObservable<NodeTypeCompileState?> Read(IMessageHub hub, string nodeTypePath) =>
        hub.GetMeshNode(StatePath(nodeTypePath), TimeSpan.FromSeconds(10))
            .Take(1)
            .Select(node => Parse(node, hub.JsonSerializerOptions));

    /// <summary>The state carried in a satellite node's <see cref="ActivityLog.ReturnValue"/>,
    /// or null when absent/unreadable. Pure.</summary>
    public static NodeTypeCompileState? Parse(MeshNode? node, JsonSerializerOptions options)
    {
        if (node is null)
            return null;
        try
        {
            return node.ContentAs<ActivityLog>(options)?.ReturnValue is { } value
                ? value.Deserialize<NodeTypeCompileState>(options)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The satellite node carrying <paramref name="state"/> — pure, unit-testable.</summary>
    public static MeshNode StateNode(
        string nodeTypePath, NodeTypeCompileState state, JsonSerializerOptions options) =>
        new(StateId, $"{nodeTypePath}/_Activity")
        {
            Name = $"Compile state ({nodeTypePath})",
            NodeType = ActivityNodeType.NodeType,
            MainNode = nodeTypePath,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.Compilation)
            {
                Id = StateId,
                HubPath = nodeTypePath,
                Status = ActivityStatus.Succeeded,
                ReturnValue = JsonSerializer.SerializeToElement(state, options),
            },
        };

    /// <summary>
    /// Installs the mirror on the per-NodeType hub: every REAL change of the node's operational
    /// members lands on the satellite; activations, authored edits and duplicate emissions write
    /// nothing. Returns the subscription for <c>RegisterForDisposal</c>.
    /// </summary>
    public static IDisposable Install(IMessageHub hub, IWorkspace workspace)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Disposable.Empty;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileStateMirror");
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        var nodeTypePath = hub.Address.Path;

        // Installed on EVERY per-node hub (like the compile watchers) — only actual
        // type-definition nodes mirror. The tolerant ContentAs would otherwise coerce
        // arbitrary content into an empty definition, and a node whose content merely
        // NAMES an operational member must not sprout a compile-state satellite.
        //
        // ONE subscription, no stored-state pre-read: the own stream does not replay to a
        // late second subscriber, so any two-stage seeding design silently never writes
        // (the first cut did exactly that). The first observed state per activation is
        // written unconditionally — the owner's no-op upsert guard makes a redundant
        // re-activation write version-free — and every subsequent write is gated by
        // DistinctUntilChanged on the serialized state (record equality is reference-based
        // for the dictionary members, so the serialized form is the value identity).
        return workspace.GetMeshNodeStream()
            .Where(NodeTypeOperationalContent.IsNodeTypeNode)
            .Select(node => NodeTypeCompileState.FromDefinition(
                node?.ContentAs<NodeTypeDefinition>(options, logger)))
            .Where(state => state is { IsEmpty: false })
            .Select(state => (State: state!, Key: JsonSerializer.Serialize(state, options)))
            .DistinctUntilChanged(entry => entry.Key)
            // One write at a time, latest state last — Concat keeps the satellite's final
            // content equal to the final observed state without interleaving.
            .Select(entry => Observable.Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => mesh.CreateOrUpdateNode(StateNode(nodeTypePath, entry.State, options)).Take(1))
                .Select(_ => true)
                .Catch<bool, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[CompileStateMirror] {NodeTypePath}: satellite write failed (state stays on the node; next change retries).",
                        nodeTypePath);
                    return Observable.Return(false);
                }))
            .Concat()
            .Subscribe(
                _ => { },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "[CompileStateMirror] {NodeTypePath}: mirror stream faulted — satellite no longer updates until the hub recycles.",
                        nodeTypePath);
                });
    }
}
