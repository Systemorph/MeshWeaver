using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Social.Test;

public class ScheduledPostPublisherTest
{
    private static PlatformCredential FakeCred(string platform = "LinkedIn") => new()
    {
        Platform = platform,
        SubjectId = "urn:li:person:test",
        AccessToken = "token-abc",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    [Fact]
    public async Task DuePost_IsPublished_AndResultApplied()
    {
        var queue = new InMemoryPublishQueue();
        var publisher = new FakePublisher();
        var bridge = new FakeBridge();
        var opts = new SocialOptions { PublishTickInterval = TimeSpan.FromMilliseconds(50) };

        var snap = new PublishableSnapshot(
            PostPath: "Systemorph/SocialMedia/p1",
            Platform: "LinkedIn",
            AuthorHandle: "roland",
            Text: "hello",
            MediaUrls: Array.Empty<string>(),
            Credential: FakeCred(),
            ScheduledAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        queue.Enqueue(snap);

        var svc = new ScheduledPostPublisher(queue, new[] { (IPlatformPublisher)publisher }, bridge, opts, NullLogger<ScheduledPostPublisher>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _ = svc.StartAsync(cts.Token);

        await WaitUntilAsync(() => publisher.PublishedCalls.Count > 0, TimeSpan.FromSeconds(2));
        await svc.StopAsync(CancellationToken.None);

        publisher.PublishedCalls.Should().HaveCount(1);
        publisher.PublishedCalls.Single().PostPath.Should().Be(snap.PostPath);
        bridge.PublishApplied.Should().HaveCount(1);
        bridge.PublishApplied.Single().Result.Urn.Should().NotBeNull();
    }

    [Fact]
    public async Task FuturePost_IsNotPublished()
    {
        var queue = new InMemoryPublishQueue();
        var publisher = new FakePublisher();
        var bridge = new FakeBridge();
        var opts = new SocialOptions { PublishTickInterval = TimeSpan.FromMilliseconds(50) };

        queue.Enqueue(new PublishableSnapshot(
            PostPath: "Systemorph/SocialMedia/p2",
            Platform: "LinkedIn",
            AuthorHandle: "roland",
            Text: "future",
            MediaUrls: Array.Empty<string>(),
            Credential: FakeCred(),
            ScheduledAt: DateTimeOffset.UtcNow.AddMinutes(10)));

        var svc = new ScheduledPostPublisher(queue, new[] { (IPlatformPublisher)publisher }, bridge, opts, NullLogger<ScheduledPostPublisher>.Instance);
        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        await svc.StopAsync(CancellationToken.None);

        publisher.PublishedCalls.Should().BeEmpty("post scheduled 10 minutes from now must not be drained");
    }

    [Fact]
    public async Task TransientFailure_Retried_AndEventuallySucceeds()
    {
        var queue = new InMemoryPublishQueue();
        var attempts = 0;
        var publisher = new FakePublisher
        {
            PublishImpl = _ =>
            {
                attempts++;
                return attempts < 2
                    ? new PublishResult(null, null, DateTimeOffset.UtcNow, Error: "transient 500")
                    : new PublishResult("urn:success", "https://x", DateTimeOffset.UtcNow);
            }
        };
        var bridge = new FakeBridge();
        // Short interval + 3 attempts; 2^1=2s backoff is the default — override via shorter Task.Delay-proof config.
        var opts = new SocialOptions { PublishTickInterval = TimeSpan.FromMilliseconds(50), MaxPublishAttempts = 3 };

        queue.Enqueue(new PublishableSnapshot(
            "p", "LinkedIn", "r", "text", Array.Empty<string>(), FakeCred(), DateTimeOffset.UtcNow.AddSeconds(-1)));

        var svc = new ScheduledPostPublisher(queue, new[] { (IPlatformPublisher)publisher }, bridge, opts, NullLogger<ScheduledPostPublisher>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _ = svc.StartAsync(cts.Token);

        await WaitUntilAsync(() => bridge.PublishApplied.Any(r => r.Result.Error is null), TimeSpan.FromSeconds(6));
        await svc.StopAsync(CancellationToken.None);

        attempts.Should().BeGreaterThanOrEqualTo(2);
        bridge.PublishApplied.Should().Contain(r => r.Result.Urn == "urn:success");
    }

    [Fact]
    public async Task UnknownPlatform_IsDropped_NotRetried()
    {
        var queue = new InMemoryPublishQueue();
        var publisher = new FakePublisher("LinkedIn");
        var bridge = new FakeBridge();
        var opts = new SocialOptions { PublishTickInterval = TimeSpan.FromMilliseconds(50) };

        queue.Enqueue(new PublishableSnapshot(
            "p", "TikTok", "r", "t", Array.Empty<string>(), FakeCred("TikTok"), DateTimeOffset.UtcNow.AddSeconds(-1)));

        var svc = new ScheduledPostPublisher(queue, new[] { (IPlatformPublisher)publisher }, bridge, opts, NullLogger<ScheduledPostPublisher>.Instance);
        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await svc.StopAsync(CancellationToken.None);

        publisher.PublishedCalls.Should().BeEmpty();
        bridge.PublishApplied.Should().BeEmpty();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) return;
            await Task.Delay(25);
        }
    }
}
