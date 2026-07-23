using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="DeckSlidesCache"/>: the deck's sibling-slide query
/// must be SHARED across the slides of one deck (one live
/// <c>IMeshService.Query</c> subscription per parent path, Replay(1) semantics
/// for late subscribers) while distinct decks each get their own query. Uses a
/// counting NSubstitute <see cref="IMeshService"/> (same stubbing idiom as
/// <c>NotificationServiceTest</c>) so no mesh needs to boot.
/// </summary>
public class DeckSlidesCacheTest
{
    /// <summary>
    /// A stubbed mesh service whose <c>Query</c> counts SUBSCRIPTIONS (not calls)
    /// per query string: each subscription increments the counter for its query,
    /// then emits one Initial snapshot with a single slide and stays open (a live
    /// query never completes).
    /// </summary>
    private static (IMeshService Mesh, ConcurrentDictionary<string, int> Subscriptions)
        MakeCountingMesh()
    {
        var subscriptions = new ConcurrentDictionary<string, int>();
        var mesh = Substitute.For<IMeshService>();
        mesh.Query<MeshNode>(Arg.Any<MeshQueryRequest>())
            .Returns(call =>
            {
                var request = call.Arg<MeshQueryRequest>();
                return Observable.Defer(() =>
                {
                    subscriptions.AddOrUpdate(request.Query, 1, (_, n) => n + 1);
                    var deck = ExtractNamespace(request.Query);
                    return Observable
                        .Return(new QueryResultChange<MeshNode>
                        {
                            ChangeType = QueryChangeType.Initial,
                            Items =
                            [
                                new MeshNode("s1", deck)
                                {
                                    Name = "Slide 1",
                                    NodeType = SlideNodeType.NodeType,
                                    Order = 1
                                }
                            ]
                        })
                        .Concat(Observable.Never<QueryResultChange<MeshNode>>());
                });
            });
        return (mesh, subscriptions);
    }

    private static string ExtractNamespace(string query) =>
        query.Split(' ')
            .First(t => t.StartsWith("namespace:", StringComparison.Ordinal))
            ["namespace:".Length..];

    private static DeckSlidesCache MakeCache(IMeshService mesh) =>
        new(() => mesh,
            _ => Observable.Never<MeshNode?>(),
            () => new JsonSerializerOptions(),
            disconnectDelay: TimeSpan.FromMinutes(3));

    [Fact]
    public void GetOrderedSlides_ConcurrentSubscribers_ShareOneQuerySubscription()
    {
        var (mesh, subscriptions) = MakeCountingMesh();
        var cache = MakeCache(mesh);

        var received = new List<IReadOnlyList<MeshNode>>();
        using var first = cache.GetOrderedSlides("DeckA").Subscribe(received.Add);
        using var second = cache.GetOrderedSlides("DeckA").Subscribe(received.Add);

        subscriptions.Values.Sum().Should().Be(1,
            "concurrent subscribers for the same deck must share ONE underlying sibling query");
        received.Should().HaveCount(2,
            "both subscribers receive the replayed ordered slide list");
        received.Should().OnlyContain(slides =>
            slides.Count == 1 && slides[0].Path == "DeckA/s1");
    }

    [Fact]
    public void GetOrderedSlides_DifferentParents_GetSeparateQueries()
    {
        var (mesh, subscriptions) = MakeCountingMesh();
        var cache = MakeCache(mesh);

        using var a1 = cache.GetOrderedSlides("DeckA").Subscribe();
        using var a2 = cache.GetOrderedSlides("DeckA").Subscribe();
        using var b = cache.GetOrderedSlides("DeckB").Subscribe();

        subscriptions.Should().HaveCount(2,
            "each parent path owns exactly one sibling query");
        subscriptions.Keys.Should().Contain(k => k.Contains("namespace:DeckA"));
        subscriptions.Keys.Should().Contain(k => k.Contains("namespace:DeckB"));
        subscriptions.Values.Should().OnlyContain(count => count == 1);
    }
}
