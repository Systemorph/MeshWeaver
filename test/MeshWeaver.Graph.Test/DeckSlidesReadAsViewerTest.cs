using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A deck is read as its VIEWER</b> (Systemorph/MeshWeaver.Plugins#2802). The sibling-slide
/// query behind a slide's counter and Prev/Next used to run as System, so every viewer saw every
/// sibling's path, name and order whatever row-level security said, and the per-deck cache
/// shared that view across users.
///
/// <para>The fixture is a real mesh with row-level security and NO public grant: a deck of three
/// slides where the viewer holds a Viewer grant on the first two only, and a stranger holds
/// nothing. The slides and grants are WRITTEN through the mesh, never seeded with
/// <c>AddMeshNodes</c>: configuration-seeded nodes are served by the static-node provider, which
/// applies no row-level security, so a seeded deck would read the same to everyone and the
/// assertions below would be vacuous. The administrator reading all three is the control that the
/// fixture really holds a slide the viewer is denied.</para>
///
/// <para>Negative control: the pre-fix code (System query, cache keyed by parent only) hands the
/// viewer all three slides and the stranger the whole deck.</para>
/// </summary>
public class DeckSlidesReadAsViewerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly AccessContext Viewer = new() { ObjectId = "deckviewer", Name = "Deck Viewer" };
    private static readonly AccessContext Stranger = new() { ObjectId = "deckstranger", Name = "Deck Stranger" };

    // ConfigureMeshBase, deliberately NOT ConfigureMesh: the default adds a Public Admin grant,
    // under which every viewer may read everything. The slide type is registered test-locally
    // (the production type is the Publish pack's dynamic Publish/Slide), so CreateNode accepts it.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(SlideNodeType.NodeType)
            {
                Name = "Slide (test-local)",
                HubConfiguration = config => config.AddMeshDataSource(s => s.WithContentType<SlideContent>()),
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// A fresh deck of three slides, s1..s3, with the viewer granted Viewer on s1 and s2 — written
    /// as the administrator, and returned once the administrator's query sees all three.
    /// </summary>
    private async Task<string> GivenADeck(CancellationToken cancellationToken)
    {
        var deck = $"{TestPartition}/deck{Guid.NewGuid():N}";
        foreach (var (id, order) in new[] { ("s1", 1), ("s2", 2), ("s3", 3) })
            await MeshService.CreateNode(new MeshNode(id, deck) { NodeType = SlideNodeType.NodeType, Order = order })
                .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        foreach (var scope in new[] { $"{deck}/s1", $"{deck}/s2" })
            await MeshService.CreateNode(new MeshNode($"{Viewer.ObjectId}_Access", $"{scope}/_Access")
                {
                    NodeType = "AccessAssignment",
                    Name = "Deck viewer",
                    MainNode = scope,
                    Content = new AccessAssignment
                    {
                        AccessObject = Viewer.ObjectId!,
                        DisplayName = Viewer.Name,
                        Roles = [new RoleAssignment { Role = "Viewer" }],
                    },
                })
                .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        return deck;
    }

    private DeckSlidesCache NewCache() =>
        new(() => MeshService,
            _ => Observable.Never<MeshNode?>(),
            () => Mesh.JsonSerializerOptions,
            () => Access);

    // Captured INSIDE the viewer's scope, subscribed OUTSIDE it — the layout host's shape, where
    // the stream is consumed in a deferred continuation with no ambient identity.
    private IObservable<IReadOnlyList<MeshNode>> SlidesAs(DeckSlidesCache cache, string deck, AccessContext viewer)
    {
        using (Access.SwitchAccessContext(viewer))
            return cache.GetOrderedSlides(deck);
    }

    private static Task<IReadOnlyList<MeshNode>> First(
        IObservable<IReadOnlyList<MeshNode>> slides, Func<IReadOnlyList<MeshNode>, bool> settled,
        CancellationToken cancellationToken) =>
        slides.Where(settled).FirstAsync().Timeout(TestTimeouts.Convergence).Await(cancellationToken);

    private static string[] Ids(IReadOnlyList<MeshNode> slides) => slides.Select(n => n.Id).ToArray();

    /// <summary>The viewer's deck holds the two slides they may read, and never the third.</summary>
    [Fact(Timeout = 90000)]
    public async Task Viewer_SeesOnlyTheSlidesTheyMayRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var deck = await GivenADeck(ct);
        (await First(SlidesAs(NewCache(), deck, TestUsers.Admin), list => list.Count >= 3, ct))
            .Should().HaveCount(3, "the administrator holds a root grant and reads the whole deck");

        var slides = await First(SlidesAs(NewCache(), deck, Viewer), list => list.Count >= 2, ct);

        Ids(slides).Should().Equal(["s1", "s2"],
            "the sibling query must run as the viewer, and the viewer holds no grant on s3");
    }

    /// <summary>A viewer with no grant on the deck reads an empty deck, not the whole deck.</summary>
    [Fact(Timeout = 90000)]
    public async Task Stranger_SeesNoSlides()
    {
        var ct = TestContext.Current.CancellationToken;
        var deck = await GivenADeck(ct);
        (await First(SlidesAs(NewCache(), deck, TestUsers.Admin), list => list.Count >= 3, ct))
            .Should().HaveCount(3, "the administrator reads the whole deck, so the store holds it");

        var slides = await First(SlidesAs(NewCache(), deck, Stranger), _ => true, ct);

        Ids(slides).Should().BeEmpty("a viewer with no grant on any slide may read none of them");
    }

    /// <summary>
    /// The per-deck cache never serves one viewer's deck to another: the administrator's warm
    /// three-slide entry stays subscribed while the viewer asks the SAME cache for the same deck.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task Cache_DoesNotShareAnEntryAcrossViewers()
    {
        var ct = TestContext.Current.CancellationToken;
        var deck = await GivenADeck(ct);
        var cache = NewCache();
        using var adminDeck = new ReplaySubject<IReadOnlyList<MeshNode>>(1);
        using var adminSubscription = SlidesAs(cache, deck, TestUsers.Admin).Subscribe(adminDeck);
        Ids(await First(adminDeck, list => list.Count >= 3, ct)).Should().Equal(["s1", "s2", "s3"],
            "the administrator holds a root grant and reads the whole deck");

        var viewer = await First(SlidesAs(cache, deck, Viewer), list => list.Count >= 2, ct);

        Ids(viewer).Should().Equal(["s1", "s2"],
            "a warm entry for another viewer must never be replayed to this one");
    }
}
