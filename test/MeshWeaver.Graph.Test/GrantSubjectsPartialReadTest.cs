using System.Reactive.Linq;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The directory read behind the grant-subjects picker says when it was PARTIAL and how it ranked;
/// the provider must pass that on, or a partial read surfaces as a complete user list and the
/// picker ignores relevance.
/// </summary>
public class GrantSubjectsPartialReadTest
{
    private static MeshNode User(string id) => new(id, "") { Name = id, NodeType = "User" };

    private static QueryResultChange<MeshNode> Frame(QueryChangeType type, params MeshNode[] items) => new()
    {
        ChangeType = type,
        Items = items,
        Query = ParsedQuery.Empty,
    };

    private static Task<GrantSubjectsQueryProvider.Folded> Fold(params QueryResultChange<MeshNode>[] frames)
        => GrantSubjectsQueryProvider.Rows(frames.ToObservable()).LastAsync()
            .Await(TestContext.Current.CancellationToken);

    [Fact]
    public async Task ASnapshotIncompleteFrame_IsCarriedToTheEmittedChange()
    {
        var folded = await Fold(Frame(QueryChangeType.Initial, User("a")) with
        {
            SnapshotIncomplete = true,
            SilentProviders = ["Postgres"],
        });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(null, folded, ParsedQuery.Empty).Single();

        emitted.SnapshotIncomplete.Should().BeTrue();
        emitted.SilentProviders.Should().Equal("Postgres");
    }

    [Fact]
    public async Task Scores_AreCarriedAlignedWithTheItems()
    {
        var folded = await Fold(Frame(QueryChangeType.Initial, User("a"), User("b")) with { Scores = [0.2, 0.9] });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(null, folded, ParsedQuery.Empty).Single();

        emitted.Scores.Should().Equal(0.2, 0.9);
    }

    [Fact]
    public async Task ACompletenessChangeAlone_IsAnnouncedAsAnEmptyUpdate()
    {
        var before = await Fold(Frame(QueryChangeType.Initial, User("a")));
        var after = await Fold(
            Frame(QueryChangeType.Initial, User("a")),
            Frame(QueryChangeType.Updated) with { SnapshotIncomplete = true });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(before, after, ParsedQuery.Empty).Single();

        emitted.ChangeType.Should().Be(QueryChangeType.Updated);
        emitted.Items.Should().BeEmpty();
        emitted.SnapshotIncomplete.Should().BeTrue();
    }

    [Fact]
    public async Task ARestart_ForgetsThePreviousFramesIncompleteness()
    {
        var folded = await Fold(
            Frame(QueryChangeType.Initial, User("a")) with { SnapshotIncomplete = true },
            Frame(QueryChangeType.Reset, User("a")));

        folded.SnapshotIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task AReRankOfTheSameRows_IsAnnouncedAsAnUpdate()
    {
        var before = await Fold(Frame(QueryChangeType.Initial, User("a"), User("b")) with { Scores = [0.2, 0.9] });
        var after = await Fold(
            Frame(QueryChangeType.Initial, User("a"), User("b")) with { Scores = [0.2, 0.9] },
            Frame(QueryChangeType.Updated, User("a"), User("b")) with { Scores = [0.8, 0.9] });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(before, after, ParsedQuery.Empty).Single();

        emitted.ChangeType.Should().Be(QueryChangeType.Updated);
        emitted.Items.Select(n => n.Path).Should().Equal("a");
        emitted.Scores.Should().Equal(0.8);
    }

    [Fact]
    public async Task APartialRanking_EmitsNoScoresRatherThanZeros()
    {
        var folded = await Fold(
            Frame(QueryChangeType.Initial, User("a")) with { Scores = [0.5] },
            Frame(QueryChangeType.Added, User("b")));

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(null, folded, ParsedQuery.Empty).Single();

        emitted.Scores.Should().BeNull();
    }
}
