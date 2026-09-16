using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// Default <see cref="IMeshNodeEditor"/>. Reads AND writes through the one handle —
/// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace,string)"/> (auto-routes
/// own/remote), subscribing for the live value and calling <see cref="MeshNodeStreamHandle.Update"/>
/// for the change. No <c>await</c>, no <c>Task.FromResult</c>; pure observable composition.
///
/// <para>The write used to be a bespoke <c>DataChangeRequest</c> posted at the node's hub, and this
/// remark used to name the <c>[Obsolete]</c> <c>UpdateMeshNode</c> — whose own obsolete message
/// points at the handle this class now uses. One path for both directions is the point: a second
/// path to the same node is how the two drift, and it is why the router ever appeared on an end of
/// an edit (<see href="https://github.com/Systemorph/MeshWeaver/issues/1140">#1140</see>).</para>
/// </summary>
public sealed class MeshNodeEditor : IMeshNodeEditor
{
    private readonly IMessageHub hub;
    private readonly IWorkspace workspace;
    private readonly BehaviorSubject<MeshNode?> node = new(null);
    private IDisposable? activeSub;

    /// <summary>Creates an editor for the MeshNode at <paramref name="path"/>. Subscribes immediately.</summary>
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

    /// <inheritdoc />
    public string CurrentPath { get; private set; }

    /// <inheritdoc />
    public IObservable<MeshNode> Node =>
        node.Where(n => n != null).Select(n => n!);

    /// <inheritdoc />
    public void Update(Func<MeshNode, MeshNode> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        // 🚨 WRITES GO BACK THROUGH THE SAME STREAM THIS EDITOR READS (#1140, Copilot on #4487).
        //
        // This used to post a bespoke `DataChangeRequest` at the node's own hub, which was the one
        // place in this class that did NOT use the stream it is built on: the read half has always
        // gone through `workspace.GetMeshNodeStream(CurrentPath)`, so the write was a second path to
        // the same node — the shape the framework's mutation rule exists to remove, and the reason
        // the router ever appeared on an end here at all. `MeshNodeStreamHandle.Update` routes the
        // change through `IMeshNodeStreamCache`, whose own `cache/{meshId}` hub is off the router by
        // construction, so this is a stronger fix than stamping a different sender on the same
        // bespoke post: the exchange stops existing rather than moving.
        //
        // Hopping it onto NodeOperationIssuingHub() was the first fix, and it left the bespoke post
        // in place. `Move` below still uses that seam, correctly — MoveNodeRequest is node LIFECYCLE
        // and has no stream equivalent.
        //
        // The snapshot guard stays: with no value yet this editor has nothing to transform, which is
        // the documented contract ("no callback needed for happy-path UI updates" presumes a
        // snapshot). The write itself still resolves its own base from the stream.
        var current = node.Value;
        if (current is null) return;

        // 🚨 COLD — the side effect runs on Subscribe, so an unsubscribed Update silently does
        // nothing (and `Update` returns void, so the caller cannot subscribe for us). Errors are
        // LOGGED rather than pushed into `node`: OnError would terminate the editor's own live
        // subscription permanently, turning one refused write into a dead editor.
        workspace.GetMeshNodeStream(CurrentPath)
            .Update(transform)
            .Subscribe(
                _ => { },
                ex => hub.ServiceProvider.GetService<ILogger<MeshNodeEditor>>()?.LogWarning(
                    ex, "MeshNodeEditor update failed for {Path}", CurrentPath));
    }

    /// <inheritdoc />
    public IObservable<MoveNodeResponse> Move(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path must be non-empty.", nameof(targetPath));

        return Observable.Create<MoveNodeResponse>(observer =>
        {
            // 🚨 Off-router issuing (#1140). This exchange is deliberately SELF-targeted — posted
            // and handled on one hub, with the reply observed on the delivery it returns — so the
            // hop has to move both ends together or it would break that pairing. When `hub` is the
            // root mesh hub (the ROUTER) the move used to run on the routing action block and be
            // stamped `mesh/{id}` at both ends; it now runs on the mesh's off-router node-operation
            // hub, which carries the same permission evaluator and type registry. For every other
            // hub NodeOperationIssuingHub is the identity function, so nothing moves.
            var issuingHub = hub.NodeOperationIssuingHub();
            var delivery = issuingHub.Post(new MoveNodeRequest(CurrentPath, targetPath),
                o => o.WithTarget(issuingHub.Address));
            if (delivery == null)
            {
                observer.OnError(new InvalidOperationException("Move: hub.Post returned null."));
                return Disposable.Empty;
            }
            issuingHub.Observe(delivery)
                .Subscribe(
                    response =>
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
                        else
                        {
                            observer.OnError(new InvalidOperationException(
                                $"Move: unexpected response {response.Message?.GetType().Name}"));
                        }
                    },
                    observer.OnError);
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

    /// <inheritdoc />
    public void Dispose()
    {
        activeSub?.Dispose();
        node.OnCompleted();
        node.Dispose();
    }
}
