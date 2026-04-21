using System;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Social.Test;

public class InMemoryPublishQueueTest
{
    private static PublishableSnapshot Snap(string path, DateTimeOffset when) => new(
        path, "LinkedIn", "r", "t", Array.Empty<string>(),
        new PlatformCredential { Platform = "LinkedIn", SubjectId = "x", AccessToken = "a" },
        when);

    [Fact]
    public void DrainDue_ReturnsAndRemovesDueItems()
    {
        var q = new InMemoryPublishQueue();
        var now = DateTimeOffset.UtcNow;
        q.Enqueue(Snap("a", now.AddSeconds(-1)));
        q.Enqueue(Snap("b", now.AddMinutes(10)));

        var due = q.DrainDue(now);
        due.Select(s => s.PostPath).Should().BeEquivalentTo(new[] { "a" });

        // After drain, "a" is gone; second drain would return empty — "b" still pending.
        q.DrainDue(now).Should().BeEmpty();
    }

    [Fact]
    public void Enqueue_DedupsByPostPath()
    {
        var q = new InMemoryPublishQueue();
        var now = DateTimeOffset.UtcNow;
        q.Enqueue(Snap("a", now.AddMinutes(10)));
        q.Enqueue(Snap("a", now.AddSeconds(-1))); // same path, becomes due — replaces the future entry

        var due = q.DrainDue(now);
        due.Should().HaveCount(1);
        due[0].PostPath.Should().Be("a");
    }
}
