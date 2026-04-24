using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Reactive editor for a single <see cref="MeshNode"/> at a known path. Owns one
/// long-lived subscription to the node's MeshNode stream so callers see live
/// updates from every source (their own saves, peer clients, server-side
/// changes). Saves push through the same stream — the echo flows back into
/// <see cref="Node"/>, so callers don't need to track in-flight state by hand.
/// </summary>
public interface IMeshNodeEditor : IDisposable
{
    /// <summary>
    /// Live observable of the node's current state. Hot — replays the latest
    /// snapshot to every new subscriber. Re-points to the new path after a
    /// successful <see cref="Move"/>.
    /// </summary>
    IObservable<MeshNode> Node { get; }

    /// <summary>
    /// The path the editor is currently subscribed to. Updated on successful
    /// <see cref="Move"/>; otherwise constant for the editor's lifetime.
    /// </summary>
    string CurrentPath { get; }

    /// <summary>
    /// Apply <paramref name="transform"/> to the node's current state and push
    /// the result through the owning hub's stream. The active subscription on
    /// <see cref="Node"/> receives the echo when persistence completes — no
    /// callback needed for happy-path UI updates.
    /// </summary>
    void Update(Func<MeshNode, MeshNode> transform);

    /// <summary>
    /// Move the node to <paramref name="targetPath"/>. On success, re-subscribes
    /// <see cref="Node"/> to the new path. The returned observable emits exactly
    /// one <see cref="MoveNodeResponse"/> (success or failure) and completes.
    /// </summary>
    IObservable<MoveNodeResponse> Move(string targetPath);
}

/// <summary>
/// Default <see cref="IMeshNodeEditor"/>. Subscribes via
/// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace,string)"/>
/// (auto-routes own/remote) and writes via
/// <see cref="MeshNodeStreamExtensions.UpdateMeshNode"/>. No <c>await</c>, no
/// <c>Task.FromResult</c>; pure observable composition.
/// </summary>
public sealed class MeshNodeEditor : IMeshNodeEditor
{
    private readonly IMessageHub hub;
    private readonly IWorkspace workspace;
    private readonly BehaviorSubject<MeshNode?> node = new(null);
    private IDisposable? activeSub;

    public MeshNodeEditor(IMessageHub hub, string path)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must be non-empty.", nameof(path));
        this.hub = hub;
        this.workspace = hub.GetWorkspace();
        this.CurrentPath = path;
        SubscribeToCurrentPath();
    }

    public string CurrentPath { get; private set; }

    public IObservable<MeshNode> Node =>
        node.Where(n => n != null).Select(n => n!);

    public void Update(Func<MeshNode, MeshNode> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        workspace.UpdateMeshNode(transform, new Address(CurrentPath), CurrentPath);
    }

    public IObservable<MoveNodeResponse> Move(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path must be non-empty.", nameof(targetPath));

        return Observable.Create<MoveNodeResponse>(observer =>
        {
            var delivery = hub.Post(new MoveNodeRequest(CurrentPath, targetPath),
                o => o.WithTarget(hub.Address));
            hub.RegisterCallback(delivery, response =>
            {
                if (response.Message is MoveNodeResponse moveResp)
                {
                    if (moveResp.Success)
                    {
                        CurrentPath = targetPath;
                        SubscribeToCurrentPath();
                    }
                    observer.OnNext(moveResp);
                    observer.OnCompleted();
                }
                return response;
            });
            return Disposable.Empty;
        });
    }

    private void SubscribeToCurrentPath()
    {
        activeSub?.Dispose();
        activeSub = workspace.GetMeshNodeStream(CurrentPath).Subscribe(
            n => node.OnNext(n),
            ex => node.OnError(ex));
    }

    public void Dispose()
    {
        activeSub?.Dispose();
        node.OnCompleted();
        node.Dispose();
    }
}
