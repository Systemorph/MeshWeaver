using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Code-behind for <see cref="CompileProgressIndicator"/>. When a
/// <c>NodeTypePath</c> is provided, subscribes via
/// <c>IMeshNodeStreamCache.GetStream</c> on that path and surfaces
/// progress whenever <c>NodeTypeDefinition.CompilationStatus = Compiling</c>.
/// Without a path, falls back to the global synced
/// <c>nodeType:NodeType</c> query.
///
/// <para>The single-path mode follows the canonical cache pattern: one
/// upstream subscription per path, shared across every GUI watching the
/// same NodeType. See <c>Doc/GUI/ItemTemplateMeshNodeStreamBinding</c>.</para>
/// </summary>
public partial class CompileProgressIndicator : IDisposable
{
    [Inject] private IMessageHub Hub { get; set; } = default!;

    /// <summary>
    /// Optional: restrict the indicator to a specific NodeType path. When set,
    /// the indicator only surfaces compilation progress for that exact path.
    /// When null, it watches the synced <c>nodeType:NodeType</c> query and
    /// reports the first NodeType whose
    /// <see cref="NodeTypeDefinition.CompilationStatus"/> is
    /// <see cref="CompilationStatus.Compiling"/>.
    /// </summary>
    [Parameter] public string? NodeTypePath { get; set; }

    private string? CompilingPath;
    private string? ProgressMessage;
    private string? ErrorPath;
    private string? CompileError;
    private string? StreamError;
    private int Seconds;
    private IDisposable? _statusSub;
    private IDisposable? _activitySub;
    private IDisposable? _tickSub;

    /// <summary>
    /// Snapshot of what the watched NodeType(s) are doing — drives the render
    /// branches: an in-flight compile, a terminal failure, or nothing.
    /// </summary>
    private sealed record CompileState(
        string? Path, CompilationStatus? Status, string? ActivityPath, string? Error);

    /// <summary>
    /// Starts the compile-state subscription. In single-path mode subscribes to
    /// <c>NodeTypePath</c>'s mesh node stream; in global mode queries the
    /// <c>nodeType:NodeType</c> synced query. Also wires the per-second elapsed-time
    /// ticker and the live activity message subscription for in-flight compiles.
    /// </summary>
    protected override void OnInitialized()
    {
        // Surface BOTH an in-flight compile (spinner + live activity message)
        // AND a terminal failure (the CompilationError text). The activity path
        // is written onto the NodeType at compile start
        // (NodeTypeCompileActivityHandler → LastCompilationActivityPath), so it is
        // live throughout the compile — we follow it to surface real progress.
        // 🚨 Errors are surfaced, never swallowed: a stuck/blank layout area must
        // tell the user WHY it isn't loading (compile failed) instead of showing
        // an indefinite spinner with no message.
        IObservable<CompileState> compileObs;
        if (!string.IsNullOrEmpty(NodeTypePath))
        {
            // Single-NodeType mode: subscribe to that node's live stream
            // through IMeshNodeStreamCache — process-wide shared handle,
            // joined by every GUI watching the same NodeType.
            compileObs = Hub.GetMeshNodeStream(NodeTypePath)
                .Select(n => n.ContentAs<NodeTypeDefinition>(Hub.JsonSerializerOptions) is { } def
                             && def.CompilationStatus is CompilationStatus.Compiling or CompilationStatus.Error
                    ? new CompileState(NodeTypePath, def.CompilationStatus,
                        def.LastCompilationActivityPath, def.CompilationError)
                    : new CompileState(null, null, null, null));
        }
        else
        {
            // Global mode: synced NodeType query — replaces the old 1-second
            // INodeTypeService.GetCompilingPaths poll. Query is the right
            // primitive here (set of nodes); single-node cache wouldn't apply.
            // Prefer an in-flight compile; fall back to a failed one so a layout
            // area that won't load surfaces the compile error rather than a blank.
            var workspace = Hub.GetWorkspace();
            compileObs = workspace.GetQuery("nodetypes-compiling", "nodeType:NodeType")
                .Select(snapshot =>
                {
                    var node = snapshot.FirstOrDefault(n => n.ContentAs<NodeTypeDefinition>(Hub.JsonSerializerOptions) is { } d
                            && d.CompilationStatus == CompilationStatus.Compiling)
                        ?? snapshot.FirstOrDefault(n => n.ContentAs<NodeTypeDefinition>(Hub.JsonSerializerOptions) is { } d
                            && d.CompilationStatus == CompilationStatus.Error);
                    return node.ContentAs<NodeTypeDefinition>(Hub.JsonSerializerOptions) is { } def
                        ? new CompileState(node.Path, def.CompilationStatus,
                            def.LastCompilationActivityPath, def.CompilationError)
                        : new CompileState(null, null, null, null);
                });
        }

        _statusSub = compileObs
            .DistinctUntilChanged()
            .Subscribe(
                state =>
                {
                    var compiling = state.Status == CompilationStatus.Compiling;
                    CompilingPath = compiling ? state.Path : null;
                    ErrorPath = state.Status == CompilationStatus.Error ? state.Path : null;
                    CompileError = state.Status == CompilationStatus.Error ? state.Error : null;
                    ProgressMessage = null;
                    StreamError = null;
                    Seconds = 0;
                    StartOrStopTicker(compiling);
                    SubscribeToActivity(state.ActivityPath, compiling);
                    InvokeAsync(StateHasChanged);
                },
                // 🚨 Surface, don't swallow. A faulted status stream means we can
                // no longer report compile state — say so instead of going blank.
                ex =>
                {
                    StreamError = ex.Message;
                    CompilingPath = null;
                    ErrorPath = null;
                    InvokeAsync(StateHasChanged);
                });
    }

    /// <summary>
    /// Follow the compile <see cref="ActivityLog"/> and surface its live message
    /// tail ("starting Roslyn", "Roslyn produced assembly", "Release created") so
    /// the user sees real progress, not just a blind spinner. Best-effort: a
    /// missing/slow activity simply leaves the generic "Compiling …" text.
    /// </summary>
    private void SubscribeToActivity(string? activityPath, bool active)
    {
        _activitySub?.Dispose();
        _activitySub = null;
        if (!active || string.IsNullOrEmpty(activityPath)) return;

        _activitySub = Hub.GetMeshNodeStream(activityPath)
            .Select(n => n.ContentAs<ActivityLog>(Hub.JsonSerializerOptions)?.Messages is { Count: > 0 } msgs
                ? msgs[^1].Message
                : null)
            .DistinctUntilChanged()
            .Subscribe(
                msg =>
                {
                    ProgressMessage = msg;
                    InvokeAsync(StateHasChanged);
                },
                // Activity progress is best-effort, but still surface the fault as
                // the progress tail rather than swallowing it silently.
                ex =>
                {
                    ProgressMessage = $"(compile progress unavailable: {ex.Message})";
                    InvokeAsync(StateHasChanged);
                });
    }

    private void StartOrStopTicker(bool active)
    {
        _tickSub?.Dispose();
        _tickSub = null;
        if (!active) return;
        _tickSub = Observable.Interval(TimeSpan.FromSeconds(1))
            .Subscribe(_ =>
            {
                Seconds++;
                InvokeAsync(StateHasChanged);
            });
    }

    /// <summary>
    /// Disposes the compile-state, activity-progress, and elapsed-time ticker subscriptions.
    /// </summary>
    public void Dispose()
    {
        _statusSub?.Dispose();
        _activitySub?.Dispose();
        _tickSub?.Dispose();
    }
}
