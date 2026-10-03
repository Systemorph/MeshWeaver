using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Durable streams on Orleans (Doc/Architecture/DurableStreams): the consuming node and the
/// stream node are GRAIN-hosted hubs on the silo, the producer is a hub on the Orleans CLIENT, and
/// every request between them crosses the cluster transport. Publish 40, recycle the consuming
/// grain's hub mid-stream, publish 20 more while it is gone — every item is processed exactly once,
/// in order, by the hubs that come back. The same hub code as the Monolith case
/// (<c>DurableStreamConsumerTest</c>); only the host differs.
/// </summary>
public class OrleansDurableStreamConsumerTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(DurableStreamSiloConfigurator);

    [Fact(Timeout = 180_000)]
    public async Task RecycleOfAGrainHostedConsumerMidStream_EveryItemIsProcessedExactlyOnceInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloMesh = SiloServices().GetRequiredService<IMessageHub>();
        var access = siloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var log = SiloServices().GetRequiredService<DurableStreamProbeLog>();
        var owner = $"dstream/{Guid.NewGuid():N}";

        await access.RunAsSystem(() => siloMesh.ServiceProvider.GetRequiredService<IMeshService>()
                .CreateNode(MeshNode.FromPath(owner) with
                {
                    Name = "A grain-hosted durable stream consumer",
                    NodeType = DurableStreamSiloConfigurator.ProbeType,
                    State = MeshNodeState.Active,
                }))
            .Should().Within(TestTimeouts.CrossSilo).Emit("the consuming node is created", ct);

        var client = GetClient();
        var stream = new DurableStreamId(DurableStreamSiloConfigurator.Family, owner);

        // The first item WAKES the consumer: nobody opens the node, the orphaned stream does.
        await Publish(client, stream, 1, 40, ct);
        await log.Of(owner).Should().Within(TestTimeouts.CrossSilo)
            .Match(l => l.Count >= 12, "the grain-hosted consumer is part-way through the stream when the recycle hits", ct);

        // As SYSTEM: the node sits in a partition the test identity holds no grant on; the subject
        // here is the consumer's resume, not the access gate.
        await client.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => client.RecycleNode(owner, reason: "OrleansDurableStreamConsumerTest: recycle the consumer mid-stream"))
            .Should().Within(TestTimeouts.CrossSilo).Emit("the consuming grain's hub recycles", ct);
        await Publish(client, stream, 41, 60, ct);

        await log.Of(owner).Should().Within(TestTimeouts.CrossSilo)
            .Match(l => l.Contains(60), "the consumer that came back reaches the last item", ct);
        var processed = await log.Of(owner).Throttle(TimeSpan.FromSeconds(2))
            .Should().Within(TestTimeouts.CrossSilo).Emit("the consumer goes quiet", ct);
        processed.Should().Equal(Enumerable.Range(1, 60).Select(i => (long)i),
            "every item exactly once and in order across the recycle of a grain-hosted consumer");
    }


    private static async Task Publish(IMessageHub client, DurableStreamId stream, int from, int to, CancellationToken ct)
    {
        for (var i = from; i <= to; i++)
        {
            var sequence = await client.PublishDurable(stream, new DurableStreamProbeEvent(i))
                .Should().Within(TestTimeouts.CrossSilo).Emit($"item {i} is durable", ct);
            sequence.Should().Be(i, "the stream's hub assigns contiguous sequences from 1");
        }
    }
}

/// <summary>The probe payload.</summary>
/// <param name="Value">The value published (equals the expected sequence).</param>
public sealed record DurableStreamProbeEvent(int Value);

/// <summary>
/// What the grain-hosted probe handlers saw. A singleton of the silo's mesh (never static), read by
/// the test through the silo's services.
/// </summary>
public sealed class DurableStreamProbeLog
{
    private ImmutableDictionary<string, ImmutableList<long>> entries = ImmutableDictionary<string, ImmutableList<long>>.Empty;
    private readonly ReplaySubject<string> changed = new();

    /// <summary>Records the item, then "processes" it for a moment so a recycle lands mid-item.</summary>
    public IObservable<Unit> Handle(string key, DurableStreamItem<DurableStreamProbeEvent> item)
    {
        ImmutableInterlocked.Update(ref entries, d => d.SetItem(key, d.GetValueOrDefault(key, ImmutableList<long>.Empty).Add(item.Sequence)));
        changed.OnNext(key);
        return Observable.Timer(TimeSpan.FromMilliseconds(15)).Select(_ => Unit.Default);
    }

    /// <summary>The processed sequences of one consumer, re-emitted on every change.</summary>
    public IObservable<ImmutableList<long>> Of(string key)
        => changed.Where(k => k == key)
            .Select(_ => Volatile.Read(ref entries).GetValueOrDefault(key, ImmutableList<long>.Empty))
            .StartWith(Volatile.Read(ref entries).GetValueOrDefault(key, ImmutableList<long>.Empty));
}

/// <summary>The standard silo plus the probe NodeType whose every activation consumes its own stream.</summary>
public class DurableStreamSiloConfigurator : SharedSiloConfigurator
{
    /// <summary>The probe NodeType.</summary>
    public const string ProbeType = "OrleansDurableStreamProbe";

    /// <summary>The stream family the probe consumes.</summary>
    public const string Family = "ProbeStream";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder) => builder
        .ConfigureServices(services => services.AddSingleton<DurableStreamProbeLog>())
        .AddMeshNodes(new MeshNode(ProbeType)
        {
            Name = "Orleans durable stream consumer probe",
            HubConfiguration = config => config
                .AddMeshDataSource()
                .WithType(typeof(DurableStreamProbeEvent), nameof(DurableStreamProbeEvent))
                .WithDurableStreamConsumer<DurableStreamProbeEvent>(
                    Family,
                    hub => hub.Address.Path,
                    (hub, item) => hub.ServiceProvider.GetRequiredService<DurableStreamProbeLog>().Handle(hub.Address.Path, item)),
        });
}
