using System;
using System.Collections.Generic;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Verifies that <see cref="DataChangeNotifier"/> drops local-write echoes via
/// <see cref="DataChangeNotificationExtensions.DistinctByPathVersion"/>. The scenario
/// it protects: when the same process writes a row AND receives the PG LISTEN/NOTIFY
/// echo of that write, both events carry the same (Path, Version) and the second
/// must be filtered so subscribers don't see the change twice.
/// </summary>
public class DataChangeNotifierDedupTest
{
    [Fact]
    public void DropsEchoWithSamePathAndVersion()
    {
        using var notifier = new DataChangeNotifier();
        var seen = new List<DataChangeNotification>();
        using var sub = notifier.Subscribe(seen.Add);

        // Local write publishes — workspace cache update fires this.
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 7));
        // PG LISTEN/NOTIFY echo arrives — same path, same version.
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 7));

        seen.Should().HaveCount(1, "the echo with matching (path, version) must be deduped");
        seen[0].Version.Should().Be(7);
    }

    [Fact]
    public void EmitsHigherVersionAfterFirst()
    {
        using var notifier = new DataChangeNotifier();
        var seen = new List<DataChangeNotification>();
        using var sub = notifier.Subscribe(seen.Add);

        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 7));
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 7)); // echo
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 8)); // real new write

        seen.Should().HaveCount(2);
        seen[0].Version.Should().Be(7);
        seen[1].Version.Should().Be(8);
    }

    [Fact]
    public void DropsStaleVersionsLowerThanLastSeen()
    {
        using var notifier = new DataChangeNotifier();
        var seen = new List<DataChangeNotification>();
        using var sub = notifier.Subscribe(seen.Add);

        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 10));
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 5)); // out-of-order echo
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 11));

        seen.Should().HaveCount(2);
        seen[0].Version.Should().Be(10);
        seen[1].Version.Should().Be(11);
    }

    [Fact]
    public void TracksVersionPerPathIndependently()
    {
        using var notifier = new DataChangeNotifier();
        var seen = new List<DataChangeNotification>();
        using var sub = notifier.Subscribe(seen.Add);

        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 7));
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story2", null, version: 7)); // different path, same version — emits
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null, version: 7)); // echo of Story1

        seen.Should().HaveCount(2);
        seen[0].Path.Should().Be("acme/Story1");
        seen[1].Path.Should().Be("acme/Story2");
    }

    [Fact]
    public void PassesThroughWhenVersionIsUnknown()
    {
        using var notifier = new DataChangeNotifier();
        var seen = new List<DataChangeNotification>();
        using var sub = notifier.Subscribe(seen.Add);

        // Version = -1 means "no version available" (in-memory adapter, FS watcher,
        // legacy NOTIFY trigger, DELETE without NEW row). Always pass through.
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null));
        notifier.NotifyChange(DataChangeNotification.Updated("acme/Story1", null));
        notifier.NotifyChange(DataChangeNotification.Deleted("acme/Story1"));

        seen.Should().HaveCount(3, "events without a version cannot be deduped — they pass through");
    }
}
