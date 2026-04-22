using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Social.Test;

public class PostStatsRefresherTest
{
    private sealed class StaticSource : IStatsRefreshSource
    {
        public IReadOnlyList<StatsRefreshTarget> Targets { get; init; } = Array.Empty<StatsRefreshTarget>();
        public async IAsyncEnumerable<StatsRefreshTarget> GetDueRefreshesAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var t in Targets)
            {
                yield return t;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task Stats_AreFetched_PerTarget_AndAppliedToBridge()
    {
        var publisher = new FakePublisher
        {
            StatsImpl = urn => new PostStats(
                Impressions: 100, Likes: 5, Comments: 2, Shares: 1,
                RetrievedAt: DateTimeOffset.UtcNow)
        };
        var bridge = new FakeBridge();
        var source = new StaticSource
        {
            Targets = new[]
            {
                new StatsRefreshTarget("p1", "LinkedIn", "urn:li:share:1",
                    new PlatformCredential { Platform = "LinkedIn", SubjectId = "x", AccessToken = "t" }),
                new StatsRefreshTarget("p2", "LinkedIn", "urn:li:share:2",
                    new PlatformCredential { Platform = "LinkedIn", SubjectId = "x", AccessToken = "t" }),
            }
        };
        var opts = new SocialOptions { StatsTickInterval = TimeSpan.FromMilliseconds(50) };

        var svc = new PostStatsRefresher(source, new[] { (IPlatformPublisher)publisher }, bridge, opts, NullLogger<PostStatsRefresher>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _ = svc.StartAsync(cts.Token);

        await WaitUntilAsync(() => bridge.StatsApplied.Count >= 2, TimeSpan.FromSeconds(2));
        await svc.StopAsync(CancellationToken.None);

        bridge.StatsApplied.Select(s => s.PostPath).Should().BeEquivalentTo(new[] { "p1", "p2" });
        bridge.StatsApplied.All(s => s.Stats.Impressions == 100).Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Predicate did not become true within {timeout.TotalMilliseconds:F0} ms. " +
                    "The awaited stats-refresh side-effect never occurred.");
            await Task.Delay(25);
        }
    }
}
