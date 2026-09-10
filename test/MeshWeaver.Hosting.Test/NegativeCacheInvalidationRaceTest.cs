using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression guard for issue #3954: a missing-node verdict that was already in flight when a
/// write invalidated the path must not re-open the negative-cache window after that invalidation.
/// </summary>
public class NegativeCacheInvalidationRaceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    /// <summary>
    /// Owns one throwaway path and parks its first request before returning the authoritative
    /// routing NotFound. The producer-to-test signal is an <see cref="AsyncSubject{T}"/>; the
    /// release into the deliberately parked routing callback is the sanctioned bounded
    /// <see cref="SpinWait.SpinUntil(Func{bool},TimeSpan)"/> shape and is always opened by the
    /// test's <c>finally</c>.
    /// </summary>
    private sealed class HeldMissingOwner : IDisposable
    {
        private readonly IDisposable registration;
        private readonly AsyncSubject<Unit> requestEntered = new();
        private int release;
        private int gateTimedOut;

        public HeldMissingOwner(IMessageHub mesh, IRoutingService routing, string path)
        {
            var access = mesh.ServiceProvider.GetService<AccessService>();
            SyncDelivery answer = delivery =>
            {
                if (delivery.Message is DeliveryFailure
                    || delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
                    return delivery.Processed();

                requestEntered.OnNext(Unit.Default);
                requestEntered.OnCompleted();
                if (!SpinWait.SpinUntil(
                        () => Volatile.Read(ref release) == 1,
                        TestTimeouts.Convergence))
                    Volatile.Write(ref gateTimedOut, 1);

                var message = $"No node found at '{path}'. Closest ancestor is 'TestData'.";
                using (delivery.AccessContext is null ? access?.ImpersonateAsSystem() : null)
                    mesh.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.NotFound,
                            Message = message,
                        },
                        options => options.ResponseFor(delivery));
                return delivery.FailedAndNacked(message);
            };
            registration = routing.RegisterStream(new Address(path), answer);
        }

        public IObservable<Unit> RequestEntered => requestEntered;
        public bool GateTimedOut => Volatile.Read(ref gateTimedOut) != 0;
        public void Release() => Volatile.Write(ref release, 1);
        public void Dispose() => registration.Dispose();
    }

    private async Task<string> CreateNodeAsync()
    {
        var path = $"{TestPartition}/negative-race-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(node).Should().Within(TestTimeouts.Convergence).Emit();
        return path;
    }

    private void PublishAuthoritativeChange(string path)
    {
        var segments = path.Split('/');
        Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>().Publish(new MeshChangeEvent(
            Namespace: string.Join("/", segments[..^1]),
            Id: segments[^1],
            Path: path,
            Kind: MeshChangeKind.Created,
            NodeType: MeshNode.NodeTypePath,
            Version: 1,
            Timestamp: DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Control arm: the claim guard must not disable the storm breaker. When no authoritative
    /// change supersedes the probe, the same NotFound still opens its normal backoff window.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task MissingVerdictWithoutInvalidation_StillOpensTheNegativeWindow()
    {
        var path = await CreateNodeAsync();
        using var owner = new HeldMissingOwner(
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IRoutingService>(),
            path);
        owner.Release();

        var failure = await Cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(TestTimeouts.Convergence).Match(
                notification => notification.Kind == NotificationKind.OnError,
                "the owner must return the genuine missing-node verdict used by the subject arm");

        MeshNodeStreamCache.IsMissingNodeFailure(failure.Exception!).Should().BeTrue();
        owner.GateTimedOut.Should().BeFalse();
        Cache.IsStormWindowOpen(path).Should().BeTrue(
            "without a newer change event, a genuine missing path must retain storm backoff");
    }

    [Fact(Timeout = 240_000)]
    public async Task MissingVerdictThatLandsAfterInvalidation_DoesNotRearmTheNegativeWindow()
    {
        var path = await CreateNodeAsync();
        using var owner = new HeldMissingOwner(
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IRoutingService>(),
            path);

        var terminal = Cache.GetStream(path, Mesh.JsonSerializerOptions).Materialize().Replay();
        using var connection = terminal.Connect();
        try
        {
            await owner.RequestEntered.Should().Within(TestTimeouts.Convergence).Emit(
                "the NotFound must already be in flight before the invalidation, or the race is not held");

            PublishAuthoritativeChange(path);
            owner.Release();

            var failure = await terminal.Should().Within(TestTimeouts.Convergence).Match(
                notification => notification.Kind == NotificationKind.OnError,
                "the held owner must deliver its pre-invalidation NotFound after the change event");
            MeshNodeStreamCache.IsMissingNodeFailure(failure.Exception!).Should().BeTrue(
                "the delayed verdict must be exactly the failure class that normally opens the negative window");
            owner.GateTimedOut.Should().BeFalse(
                "the test must release the parked verdict; a self-expired gate proves no ordering");
            Cache.IsStormWindowOpen(path).Should().BeFalse(
                "the change event is authoritative over a missing verdict minted in the older failure era");
        }
        finally
        {
            owner.Release();
        }
    }
}
