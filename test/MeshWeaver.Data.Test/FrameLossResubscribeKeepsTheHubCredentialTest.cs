using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A mirror opened AS ITS HUB (<see cref="IWorkspace.GetRemoteStreamAsHub"/>) re-subscribes as its
/// hub, on the frame-loss path too.
///
/// <para>The initial subscribe of such a stream overrides whatever user is ambient with the hub's
/// own credential (<c>ImpersonateAsHub(hub.Address)</c>), and so does the end/recycle re-subscribe.
/// The frame-loss re-ask (<c>RequestFreshSnapshot</c>) posted with the captured subscriber only, so
/// a hub mirror opened while a user happened to be ambient re-subscribed as THAT USER. Under
/// row-level security an infrastructure mirror the user may not read is then refused and faulted.
/// Measured on the wire: every <see cref="SubscribeRequest"/> the client posts carries the client
/// hub's credential — the initial one AND the frame-loss re-subscribe.</para>
///
/// <para>The trigger is the deterministic wire loss <see cref="StreamFrameLossResyncTest"/> uses: eat
/// exactly one mid-burst Patch, and the mirror's gap detection posts a second SubscribeRequest.</para>
/// </summary>
public class FrameLossResubscribeKeepsTheHubCredentialTest(ITestOutputHelper output) : HubTestBase(output)
{
    private int droppedPatches;

    /// <summary>The credential each SubscribeRequest the client posted LEFT under. Instance, never static.</summary>
    private readonly ConcurrentQueue<AccessContext?> subscribeCredentials = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
                d.Message is DataChangedEvent { ChangeType: ChangeType.Patch }
                && Interlocked.Exchange(ref droppedPatches, 1) == 0
                    ? d.Ignored()
                    : next.Invoke(d)))
            // Recorded on the OWNER, as it arrives: the credential the owner's gate decides on.
            .AddDeliveryPipeline(p => p.AddPipeline((d, ct, next) =>
            {
                if (d.Message is SubscribeRequest)
                    subscribeCredentials.Enqueue(d.AccessContext);
                return next.Invoke(d, ct);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    [HubFact]
    public async Task TheFrameLossResubscribe_OfAHubMirror_LeavesAsTheHub()
    {
        var host = GetHost();
        var client = GetClient();
        var hostAccess = host.ServiceProvider.GetRequiredService<AccessService>();
        hostAccess.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });
        // A USER is ambient while the HUB mirror opens — the shape the hub override exists for.
        client.ServiceProvider.GetRequiredService<AccessService>()
            .SetContext(new AccessContext { ObjectId = "carol", Name = "Carol" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var clientStream = client.GetWorkspace()
            .GetRemoteStreamAsHub(CreateHostAddress(), new CollectionsReference(collectionName));

        await clientStream.Should().Within(10.Seconds()).Emit();

        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(hostAccess.Context!));

        await clientStream
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>()
                    .Count(d => d.Text?.StartsWith("value-") == true) == 3)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit();

        Volatile.Read(ref droppedPatches).Should().Be(1,
            "PRECONDITION: exactly one mid-burst Patch was dropped, so a frame-loss re-subscribe happened");

        var credentials = subscribeCredentials.ToArray();
        foreach (var c in credentials)
            Output.WriteLine($"SubscribeRequest left as: {c?.ObjectId ?? "(null)"}");
        credentials.Should().HaveCountGreaterThan(1,
            "PRECONDITION: the mirror RE-subscribed after the gap; one request compares nothing");
        credentials.Should().OnlyContain(c => c != null && c.ObjectId == client.Address.ToFullString(),
            "a mirror opened as its hub re-subscribes as its hub on every path — never as the user "
            + "who happened to be ambient when it was opened");
    }
}
