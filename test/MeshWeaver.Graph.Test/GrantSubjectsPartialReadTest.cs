using System.Reactive.Linq;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

    private static GrantSubjectsQueryProvider.Folded Fold(params QueryResultChange<MeshNode>[] frames)
        => GrantSubjectsQueryProvider.Rows(frames.ToObservable()).LastAsync().Wait();

    [Fact]
    public void ASnapshotIncompleteFrame_IsCarriedToTheEmittedChange()
    {
        var folded = Fold(Frame(QueryChangeType.Initial, User("a")) with
        {
            SnapshotIncomplete = true,
            SilentProviders = ["Postgres"],
        });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(null, folded, ParsedQuery.Empty).Single();

        emitted.SnapshotIncomplete.Should().BeTrue();
        emitted.SilentProviders.Should().Equal("Postgres");
    }

    [Fact]
    public void Scores_AreCarriedAlignedWithTheItems()
    {
        var folded = Fold(Frame(QueryChangeType.Initial, User("a"), User("b")) with { Scores = [0.2, 0.9] });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(null, folded, ParsedQuery.Empty).Single();

        emitted.Scores.Should().Equal(0.2, 0.9);
    }

    [Fact]
    public void ACompletenessChangeAlone_IsAnnouncedAsAnEmptyUpdate()
    {
        var before = Fold(Frame(QueryChangeType.Initial, User("a")));
        var after = Fold(
            Frame(QueryChangeType.Initial, User("a")),
            Frame(QueryChangeType.Updated) with { SnapshotIncomplete = true });

        var emitted = GrantSubjectsQueryProvider.Diff<MeshNode>(before, after, ParsedQuery.Empty).Single();

        emitted.ChangeType.Should().Be(QueryChangeType.Updated);
        emitted.Items.Should().BeEmpty();
        emitted.SnapshotIncomplete.Should().BeTrue();
    }

    [Fact]
    public void ARestart_ForgetsThePreviousFramesIncompleteness()
    {
        var folded = Fold(
            Frame(QueryChangeType.Initial, User("a")) with { SnapshotIncomplete = true },
            Frame(QueryChangeType.Reset, User("a")));

        folded.SnapshotIncomplete.Should().BeFalse();
    }
}
