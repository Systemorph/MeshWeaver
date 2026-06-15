using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the sync-stream version mechanism. The version is the ONE reliable
/// ordering signal, so it must be assigned by the OWNING hub off the clock that
/// actually ticks per update — the per-stream sync hub (<c>Hub.Version</c>) —
/// NOT the host's global clock (<c>Host.Version</c>), which can sit still while
/// the stream keeps ticking. Consequence to protect: consecutive updates to a
/// stream carry <b>strictly increasing</b> versions, so the monotonicity guard
/// in <see cref="SynchronizationStream{TStream}"/> never drops a fresh frame.
///
/// <para>Without this, a stale/equal version on a real update is mistaken for a
/// reordered straggler and dropped — the "view doesn't refresh / blank layout"
/// failure mode.</para>
/// </summary>
public class StreamVersionMonotonicityTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    [HubFact]
    public async Task OwnerAssignsStrictlyIncreasingVersions_AcrossSequentialUpdates()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName))!;

        // Record every version the OWNER stamps onto a non-null emission.
        var versions = new ReplaySubject<long>();
        using var sub = stream
            .Where(ci => ci.Value is not null)
            .Select(ci => ci.Version)
            .Subscribe(versions.OnNext);

        // Three sequential updates to the SAME entity. Each must produce a fresh,
        // strictly higher version even though the host hub may not tick in between.
        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData("v", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        // Collect the first 4 versions (initial snapshot + 3 updates) and assert
        // they are strictly increasing — the owner's monotonic clock.
        var collected = await versions
            .Take(4)
            .ToList()
            .Should().Within(15.Seconds())
            .Emit();

        collected.Should().HaveCountGreaterThanOrEqualTo(2,
            "the initial snapshot plus at least one update must emit");

        var distinctOrdered = collected.ToList();
        for (var i = 1; i < distinctOrdered.Count; i++)
            distinctOrdered[i].Should().BeGreaterThan(distinctOrdered[i - 1],
                $"version at emission {i} (={distinctOrdered[i]}) must be strictly greater than the " +
                $"previous (={distinctOrdered[i - 1]}) — the owning hub assigns a monotonic version per update; " +
                "an equal/lower version would be dropped by the monotonicity guard and the view would not refresh");
    }
}
