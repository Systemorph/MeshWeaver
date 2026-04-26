using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// Listens to <see cref="IMeshChangeFeed"/> for Approval nodes whose status flipped
/// to <see cref="ApprovalStatus.Approved"/>. When one arrives:
///   1. Asks <see cref="IApprovalPublishBridge"/> to resolve the target publishable snapshot.
///   2. If the post is due (ScheduledAt ≤ now), enqueue on <see cref="IPublishQueue"/>
///      for immediate publish by the scheduler's next tick.
///   3. Otherwise the scheduler's periodic sweep will pick it up at the scheduled time.
///
/// Runs as an <see cref="IHostedService"/> so the subscription is bound to the host
/// lifecycle — <see cref="StopAsync"/> disposes the subscription cleanly on shutdown.
/// </summary>
public sealed class ApprovalToPublishHandler : IHostedService, IDisposable
{
    private readonly IMessageHub _hub;
    private readonly IMeshChangeFeed _feed;
    private readonly IMeshService _mesh;
    private readonly IApprovalPublishBridge _bridge;
    private readonly IPublishQueue _queue;
    private readonly ILogger<ApprovalToPublishHandler>? _logger;
    private IDisposable? _subscription;

    public ApprovalToPublishHandler(
        IMessageHub hub,
        IMeshChangeFeed feed,
        IMeshService mesh,
        IApprovalPublishBridge bridge,
        IPublishQueue queue,
        ILogger<ApprovalToPublishHandler>? logger = null)
    {
        _hub = hub;
        _feed = feed;
        _mesh = mesh;
        _bridge = bridge;
        _queue = queue;
        _logger = logger;
    }

    public System.Threading.Tasks.Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _feed.Subscribe(OnChange);
        _logger?.LogInformation("ApprovalToPublishHandler subscribed to mesh change feed");
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private void OnChange(MeshChangeEvent evt)
    {
        // Filter at the cheapest level first.
        if (evt.Kind == MeshChangeKind.Deleted) return;
        if (!string.Equals(evt.NodeType, "Approval", StringComparison.OrdinalIgnoreCase)) return;

        // Reactive composition — hub.GetMeshNode is composed via .SelectMany; never
        // bridged to Task and awaited (that's a 100% deadlock surface; see
        // Doc/Architecture/AsynchronousCalls.md).
        Process(evt.Path)
            .Subscribe(
                _ => { },
                ex => _logger?.LogError(ex, "Failed to handle approval event for {Path}", evt.Path));
    }

    private IObservable<System.Reactive.Unit> Process(string approvalPath) =>
        _hub.GetMeshNode(approvalPath, TimeSpan.FromSeconds(15))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .SelectMany(approvalNode =>
            {
                if (approvalNode?.Content is not Approval approval
                    || approval.Status != ApprovalStatus.Approved)
                    return Observable.Return(System.Reactive.Unit.Default);

                return _bridge.Resolve(approval)
                    .Do(snapshot =>
                    {
                        if (snapshot is null)
                        {
                            _logger?.LogDebug("Approval {Path} approved but bridge returned no publishable snapshot — skipping", approvalPath);
                            return;
                        }
                        _queue.Enqueue(snapshot);
                        _logger?.LogInformation("Queued publish for {PostPath} on {Platform} (scheduled {ScheduledAt})",
                            snapshot.PostPath, snapshot.Platform, snapshot.ScheduledAt);
                    })
                    .Select(_ => System.Reactive.Unit.Default);
            });
}
