using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The per-node hub's initial load is ONE type-source leg over TWO waits — the durable storage read,
/// then the routing-supplied own-node stream — and the DataContext time-box can only name the leg
/// (Systemorph/MeshWeaver#1122). These are the sentences that separate the two, pinned per state so a
/// reader of a production timeout knows which investigation to start.
/// </summary>
public class MeshNodeTypeSourceInitialLoadProgressTest
{
    private const string HubPath = "Collaboration";

    [Fact]
    public void NotStarted_NamesTheRoutingLegOnly_WhenThereIsOne()
    {
        var progress = new MeshNodeTypeSource.InitialLoadProgress();

        progress.Describe(HubPath, concatenatesRoutingStream: false).Should().BeNull(
            "with no routing leg and no read started there is nothing more to say than the leg itself");
        progress.Describe(HubPath, concatenatesRoutingStream: true).Should().Be(
            "no durable seed read; the routing-supplied own-node stream has not emitted");
    }

    [Fact]
    public void ReadOutstanding_PointsAtStorage()
    {
        var progress = new MeshNodeTypeSource.InitialLoadProgress();
        progress.SeedReadStarted();

        var sentence = progress.Describe(HubPath, concatenatesRoutingStream: true);

        sentence.Should().StartWith("durable seed read of 'Collaboration' outstanding for ");
        sentence.Should().EndWith("— a storage read that has not come back",
            "an unsettled read is the storage side — the routing leg is not even subscribed yet, "
            + "because Initialize concatenates it behind the read");
    }

    [Fact]
    public void ReadSettled_RoutingSilent_PointsAtTheRoutingSide()
    {
        var progress = new MeshNodeTypeSource.InitialLoadProgress();
        progress.SeedReadStarted();
        progress.SeedSettled("found no row");
        progress.SeedSettled("completed with no row");

        var sentence = progress.Describe(HubPath, concatenatesRoutingStream: true);

        sentence.Should().StartWith("durable seed read of 'Collaboration' found no row after ",
            "the FIRST settle wins — Take(1)'s completion must not overwrite the value it completed after");
        sentence.Should().EndWith("; the routing-supplied own-node stream has not emitted");
    }

    [Fact]
    public void RoutingEmittedButNothingAccepted_SaysSo()
    {
        var progress = new MeshNodeTypeSource.InitialLoadProgress();
        progress.SeedReadStarted();
        progress.SeedSettled("found no row");
        progress.RoutingEmitted();
        progress.RoutingEmitted();

        progress.Describe(HubPath, concatenatesRoutingStream: true).Should().EndWith(
            "; the routing-supplied own-node stream emitted 2 time(s) and none was accepted",
            "a routing leg that DID emit while the leg is still pending means every emission was dropped "
            + "by the own-node gate (a null, or a stale version) — a third, distinct investigation");
    }
}
