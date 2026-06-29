using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Social;

namespace MeshWeaver.Social.Test;

/// <summary>
/// Test double for <see cref="IPlatformPublisher"/>. Records calls and lets tests
/// pre-load stats / history / next-urn. Thread-safe so it can be used from the
/// BackgroundService tick loop under test.
/// </summary>
public sealed class FakePublisher : IPlatformPublisher
{
    public string Platform { get; }

    public ConcurrentBag<PlatformPublishRequest> PublishedCalls { get; } = new();
    public ConcurrentBag<string> StatsCalls { get; } = new();
    public ConcurrentBag<string> HistoryCalls { get; } = new();

    public Func<PlatformPublishRequest, PublishResult>? PublishImpl { get; set; }
    public Func<string, PostStats>? StatsImpl { get; set; }
    public IReadOnlyList<PastPost> History { get; set; } = Array.Empty<PastPost>();

    public ConcurrentBag<string> CommentsCalls { get; } = new();
    public ConcurrentBag<string> LikesCalls { get; } = new();
    public IReadOnlyDictionary<string, IReadOnlyList<EngagementComment>> CommentsByUrn { get; set; } = new Dictionary<string, IReadOnlyList<EngagementComment>>();
    public IReadOnlyDictionary<string, IReadOnlyList<EngagementLike>> LikesByUrn { get; set; } = new Dictionary<string, IReadOnlyList<EngagementLike>>();

    public FakePublisher(string platform = "LinkedIn") => Platform = platform;

    public Task<PublishResult> PublishAsync(PlatformPublishRequest request, CancellationToken ct)
    {
        PublishedCalls.Add(request);
        var impl = PublishImpl ?? (_ => new PublishResult(
            Urn: "urn:li:share:test-" + Guid.NewGuid().ToString("N")[..8],
            PostUrl: "https://linkedin.test/",
            PublishedAt: DateTimeOffset.UtcNow));
        return Task.FromResult(impl(request));
    }

    public Task<PostStats> GetStatsAsync(string urn, PlatformCredential credential, CancellationToken ct)
    {
        StatsCalls.Add(urn);
        var impl = StatsImpl ?? (_ => new PostStats(0, 0, 0, 0, DateTimeOffset.UtcNow));
        return Task.FromResult(impl(urn));
    }

    public async IAsyncEnumerable<PastPost> ListPastPostsAsync(
        PlatformCredential credential,
        DateTimeOffset? sinceInclusive,
        int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        HistoryCalls.Add(credential.SubjectId);
        foreach (var p in History)
        {
            if (sinceInclusive is { } s && p.PublishedAt < s) continue;
            yield return p;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<EngagementComment> ListCommentsAsync(
        string urn, PlatformCredential credential, int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        CommentsCalls.Add(urn);
        if (CommentsByUrn.TryGetValue(urn, out var list))
        {
            var n = 0;
            foreach (var c in list)
            {
                if (n++ >= maxItems) yield break;
                yield return c;
                await Task.Yield();
            }
        }
    }

    public async IAsyncEnumerable<EngagementLike> ListLikesAsync(
        string urn, PlatformCredential credential, int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        LikesCalls.Add(urn);
        if (LikesByUrn.TryGetValue(urn, out var list))
        {
            var n = 0;
            foreach (var l in list)
            {
                if (n++ >= maxItems) yield break;
                yield return l;
                await Task.Yield();
            }
        }
    }
}

/// <summary>
/// Minimal fake bridge that records all applied results and lets tests seed
/// snapshots by approval-path.
/// </summary>
public sealed class FakeBridge : IApprovalPublishBridge
{
    public ConcurrentDictionary<string, PublishableSnapshot> Snapshots { get; } = new();
    public ConcurrentBag<(string PostPath, PublishResult Result)> PublishApplied { get; } = new();
    public ConcurrentBag<(string PostPath, PostStats Stats)> StatsApplied { get; } = new();

    public IObservable<PublishableSnapshot?> Resolve(MeshWeaver.Mesh.Approval approval)
    {
        Snapshots.TryGetValue(approval.PrimaryNodePath ?? "", out var snap);
        return System.Reactive.Linq.Observable.Return(snap);
    }

    public IObservable<System.Reactive.Unit> ApplyPublish(string postPath, PublishResult result)
    {
        PublishApplied.Add((postPath, result));
        return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);
    }

    public IObservable<System.Reactive.Unit> ApplyStats(string postPath, PostStats stats)
    {
        StatsApplied.Add((postPath, stats));
        return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);
    }
}
