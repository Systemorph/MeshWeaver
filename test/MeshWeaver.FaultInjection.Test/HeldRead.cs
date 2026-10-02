using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// The shape every roll case starts from: a node whose OWNER is activated on one silo and whose
/// stream is HELD open on another silo for the life of the test — the fleet watch's heartbeat and
/// the running-action resumer both hold a node exactly like this.
/// </summary>
internal sealed class HeldRead : IDisposable
{
    private readonly IDisposable _connection;
    private readonly IMessageHub _hub;

    private HeldRead(string path, IConnectableObservable<MeshNode> stream, IMeshNodeStreamCache cache,
        AccessService access, IMessageHub hub)
    {
        Path = path;
        Stream = stream;
        Cache = cache;
        Access = access;
        _hub = hub;
        _connection = stream.Connect();
    }

    /// <summary>The node's path.</summary>
    public string Path { get; }

    /// <summary>The held stream, replayed so every version it delivered can be asserted on.</summary>
    public IConnectableObservable<MeshNode> Stream { get; }

    /// <summary>The holder silo's node stream cache.</summary>
    public IMeshNodeStreamCache Cache { get; }

    /// <summary>The holder silo's access service.</summary>
    public AccessService Access { get; }

    /// <summary>
    /// Creates <c>{prefix}-{guid}/Status</c> named <c>v1</c> on silo <paramref name="owner"/>, proves it
    /// is activated THERE, and holds its stream from silo <paramref name="holder"/> until it delivers v1.
    /// </summary>
    public static async Task<HeldRead> Arrange(FaultInjectionCluster mesh, int owner, int holder, string prefix,
        CancellationToken ct, Func<string, bool>? choosePath = null)
    {
        var ownerServices = mesh.Silo(owner);
        var ownerHub = mesh.Hub(owner);
        string ns, path;
        do
        {
            ns = $"{prefix}-{Guid.NewGuid():N}";
            path = $"{ns}/Status";
        } while (choosePath is not null && !choosePath(path));
        var ownerAccess = ownerServices.GetRequiredService<AccessService>();
        await ownerAccess.RunAsSystem(() => ownerServices.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Status", ns) { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active }))
            .Should().Within(TestTimeouts.Convergence).Emit("the node is created on its owner's silo", ct);
        await ownerAccess.RunAsSystem(() => ownerHub.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"the owner answers a ping on silo {owner}", ct);
        ownerHub.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            $"the owner must be activated on silo {owner} — otherwise the roll moves nothing and the case measures nothing");

        var holderServices = mesh.Silo(holder);
        var holderHub = mesh.Hub(holder);
        var access = holderServices.GetRequiredService<AccessService>();
        var cache = holderServices.GetRequiredService<IMeshNodeStreamCache>();
        var held = new HeldRead(path,
            access.RunAsSystem(() => cache.GetStream(path, holderHub.JsonSerializerOptions)).Replay(),
            cache, access, holderHub);
        await held.Stream.Where(n => n.Name == "v1").Should().Within(TestTimeouts.Convergence)
            .Emit($"silo {holder}'s held stream receives the owner's node", ct);
        return held;
    }

    /// <summary>The write the fleet watch makes every pass: through the mesh, as the system, from the holder.</summary>
    /// <param name="name">The new name.</param>
    public IObservable<MeshNode> Write(string name)
        => Access.RunAsSystem(() => Cache.Update(Path, n => n with { Name = name }, _hub.JsonSerializerOptions));

    /// <summary>The same write, made from ANOTHER silo — the writer is not the holder.</summary>
    /// <param name="mesh">The cluster.</param>
    /// <param name="silo">The writing silo.</param>
    /// <param name="name">The new name.</param>
    public IObservable<MeshNode> WriteFrom(FaultInjectionCluster mesh, int silo, string name)
    {
        var services = mesh.Silo(silo);
        var hub = mesh.Hub(silo);
        return services.GetRequiredService<AccessService>().RunAsSystem(() =>
            services.GetRequiredService<IMeshNodeStreamCache>()
                .Update(Path, n => n with { Name = name }, hub.JsonSerializerOptions));
    }

    /// <summary>
    /// The held stream's next delivery of <paramref name="name"/>, or an error that names how the stream
    /// ENDED instead — a holder whose subscription completed or faulted reads as holding while it holds
    /// nothing, which is the silent freeze this whole family of cases is about.
    /// </summary>
    /// <param name="name">The expected name.</param>
    public IObservable<MeshNode> Delivers(string name)
        => Stream.Where(n => n.Name == name).Amb(
            Stream.Materialize()
                .Where(n => n.Kind != NotificationKind.OnNext)
                .SelectMany(n => Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"the held read ENDED ({n.Kind}: {n.Exception?.Message}) instead of delivering '{name}'"))));

    public void Dispose() => _connection.Dispose();
}
