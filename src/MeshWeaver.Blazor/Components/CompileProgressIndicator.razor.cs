using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Code-behind for <see cref="CompileProgressIndicator"/>. When a
/// <c>NodeTypePath</c> is provided, subscribes via
/// <see cref="IMeshNodeStreamCache.GetStream"/> on that path and surfaces
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
    private int Seconds;
    private IDisposable? _statusSub;
    private IDisposable? _tickSub;

    protected override void OnInitialized()
    {
        IObservable<string?> compilingPathObs;
        if (!string.IsNullOrEmpty(NodeTypePath))
        {
            // Single-NodeType mode: subscribe to that node's live stream
            // through IMeshNodeStreamCache — process-wide shared handle,
            // joined by every GUI watching the same NodeType.
            var cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
            compilingPathObs = cache.GetStream(NodeTypePath)
                .Select(n => (n?.Content is NodeTypeDefinition def
                              && def.CompilationStatus == CompilationStatus.Compiling)
                    ? NodeTypePath
                    : null);
        }
        else
        {
            // Global mode: synced NodeType query — replaces the old 1-second
            // INodeTypeService.GetCompilingPaths poll. Query is the right
            // primitive here (set of nodes); single-node cache wouldn't apply.
            var workspace = Hub.GetWorkspace();
            compilingPathObs = workspace.GetQuery("nodetypes-compiling", "nodeType:NodeType")
                .Select(snapshot => snapshot
                    .FirstOrDefault(n => n.Content is NodeTypeDefinition def
                                         && def.CompilationStatus == CompilationStatus.Compiling)
                    ?.Path);
        }

        _statusSub = compilingPathObs
            .DistinctUntilChanged()
            .Subscribe(
                path =>
                {
                    CompilingPath = path;
                    Seconds = 0;
                    StartOrStopTicker(path is not null);
                    InvokeAsync(StateHasChanged);
                },
                _ => { /* swallow — best-effort UI signal */ });
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

    public void Dispose()
    {
        _statusSub?.Dispose();
        _tickSub?.Dispose();
    }
}
