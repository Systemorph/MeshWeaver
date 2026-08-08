using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="ChangeProjection"/> — the tracked-change VIEW MODEL computed from a node's
/// version history. Nothing here touches a hub, a store or a <c>_Tracking</c> satellite: the whole
/// point of the projection is that "who changed what, when" is a pure function of the history.
/// </summary>
public class ChangeProjectionTest
{
    private static ChangeProjection.VersionStep Step(long version, string author, string text, int minutesAgo = 0)
        => new(version, author, DateTimeOffset.UnixEpoch.AddMinutes(minutesAgo), text);

    // ---- Diff ----

    [Fact(Timeout = 5000)]
    public void Diff_IdenticalTexts_ProducesNoHunks()
        => ChangeProjection.Diff("same text", "same text").Should().BeEmpty();

    [Fact(Timeout = 5000)]
    public void Diff_PureInsertion_CarriesInsertedTextAndOffsets()
    {
        var hunks = ChangeProjection.Diff("hello world", "hello brave world");

        hunks.Should().ContainSingle();
        hunks[0].BaseText.Should().BeEmpty("nothing was removed");
        hunks[0].CurrentText.Should().Be("brave ");
        hunks[0].CurrentStart.Should().Be(6);
    }

    [Fact(Timeout = 5000)]
    public void Diff_PureDeletion_CarriesRemovedTextAndOffsets()
    {
        var hunks = ChangeProjection.Diff("hello brave world", "hello world");

        hunks.Should().ContainSingle();
        hunks[0].BaseText.Should().Be("brave ");
        hunks[0].CurrentText.Should().BeEmpty("nothing was added");
    }

    /// <summary>
    /// The character-level engine would report <c>ca[t→r]</c>; the projection grows each hunk out to
    /// word boundaries through the surrounding COMMON text, so the redline reads as whole words.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void Diff_ExpandsHunksToWordBoundaries()
    {
        var hunks = ChangeProjection.Diff("the cat sat", "the car sat");

        hunks.Should().ContainSingle();
        hunks[0].BaseText.Should().Be("cat");
        hunks[0].CurrentText.Should().Be("car");
    }

    [Fact(Timeout = 5000)]
    public void Diff_SeparateEdits_StaySeparateHunks()
    {
        var hunks = ChangeProjection.Diff(
            "alpha beta gamma delta epsilon zeta",
            "alpha BETA gamma delta EPSILON zeta");

        hunks.Should().HaveCount(2);
        hunks[0].CurrentText.Should().Be("BETA");
        hunks[1].CurrentText.Should().Be("EPSILON");
    }

    /// <summary>
    /// Concatenation property: replacing each hunk's base range with its current text (from the end
    /// backwards, so the earlier offsets stay valid) must reproduce the current text exactly.
    /// </summary>
    [Theory(Timeout = 5000)]
    [InlineData("one two three", "one TWO three")]
    [InlineData("one two three", "zero one two three four")]
    [InlineData("keep this text", "keep text")]
    [InlineData("", "brand new content")]
    [InlineData("everything goes", "")]
    public void Diff_HunksReconstructTheCurrentText(string baseText, string currentText)
    {
        var hunks = ChangeProjection.Diff(baseText, currentText);

        var rebuilt = baseText;
        foreach (var hunk in hunks.OrderByDescending(h => h.BaseStart))
            rebuilt = rebuilt.Remove(hunk.BaseStart, hunk.BaseText.Length)
                             .Insert(hunk.BaseStart, hunk.CurrentText);

        rebuilt.Should().Be(currentText);
    }

    // ---- Project ----

    [Fact(Timeout = 5000)]
    public void Project_SingleStep_HasNoBaselineToDiffAgainst()
        => ChangeProjection.Project("doc", [Step(1, "alice", "text")], "text", 1).Should().BeEmpty();

    [Fact(Timeout = 5000)]
    public void Project_AttributesEachChangeToTheAuthorWhoMadeIt()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "baseline-author", "alpha bravo charlie delta"),
            Step(2, "alice", "alpha BRAVOALICE charlie delta", minutesAgo: 5),
            Step(3, "bob", "alpha BRAVOALICE charlie DELTABOB", minutesAgo: 9),
        };

        var changes = ChangeProjection.Project("doc/path", steps, steps[^1].CleanText, 3);

        changes.Should().HaveCount(2);
        var alice = changes.Single(c => c.NewText == "BRAVOALICE");
        alice.Author.Should().Be("alice");
        alice.Version.Should().Be(2);
        alice.CreatedAt.Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(5));
        alice.OriginalText.Should().Be("bravo");
        alice.ChangeType.Should().Be(TrackedChangeType.Replacement);
        alice.PrimaryNodePath.Should().Be("doc/path");

        var bob = changes.Single(c => c.NewText == "DELTABOB");
        bob.Author.Should().Be("bob");
        bob.Version.Should().Be(3);
    }

    [Fact(Timeout = 5000)]
    public void Project_ResolvesRangesAgainstTheProjectedText()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "baseline", "hello world"),
            Step(2, "alice", "hello brave world"),
        };

        var change = ChangeProjection.Project("doc", steps, "hello brave world", 2).Should().ContainSingle().Subject;

        change.EffectiveStart.Should().BeGreaterThanOrEqualTo(0);
        change.EffectiveVersion.Should().Be(2);
        "hello brave world"[change.EffectiveStart..change.EffectiveEnd].Should().Be(change.NewText);
        change.AnchorText.Should().Be("hello brave world",
            "the projection anchors on the text it was taken against so a later edit re-locates it");
    }

    [Fact(Timeout = 5000)]
    public void Project_GivesEveryChangeItsOwnStableMarkerId()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "baseline", "alpha bravo charlie delta echo"),
            Step(2, "alice", "alphaONE bravo charlie delta echoTWO"),
        };

        var first = ChangeProjection.Project("doc", steps, steps[^1].CleanText, 2);
        var second = ChangeProjection.Project("doc", steps, steps[^1].CleanText, 2);

        first.Select(c => c.MarkerId).Should().OnlyHaveUniqueItems();
        first.Select(c => c.MarkerId).Should().Equal(second.Select(c => c.MarkerId),
            "the same projection must keep the same card ids across renders");
        first.Should().AllSatisfy(c => c.MarkerId.Should().MatchRegex("^[a-z0-9]+$",
            "the id is used as an HTML attribute value and a CSS class suffix"));
    }

    /// <summary>
    /// Attribution is a documented heuristic and stays honest: a hunk that several authors touched
    /// attributes to NOBODY rather than to the wrong one.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void Project_ChangeTouchedByTwoAuthors_IsNotAttributed()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "baseline", "the quick brown fox"),
            Step(2, "alice", "the SLUGGISH brown fox"),
            Step(3, "bob", "the SLUGGISHER brown fox"),
        };

        var change = ChangeProjection.Project("doc", steps, steps[^1].CleanText, 3).Should().ContainSingle().Subject;

        change.Author.Should().BeEmpty("two authors' edits overlap this hunk — guessing one would be wrong");
    }

    [Fact(Timeout = 5000)]
    public void Project_UnchangedDocument_YieldsNoChanges()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "alice", "identical"),
            Step(2, "bob", "identical"),
        };

        ChangeProjection.Project("doc", steps, "identical", 2).Should().BeEmpty();
    }

    // ---- Revert (the inverse transition a projected change offers) ----

    [Fact(Timeout = 5000)]
    public void Revert_PutsTheBaselineTextBack()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "baseline", "the quick brown fox"),
            Step(2, "alice", "the SLOW brown fox"),
        };
        var current = steps[^1].CleanText;

        var change = ChangeProjection.Project("doc", steps, current, 2).Should().ContainSingle().Subject;
        var resolved = ChangeRendering.ResolveEffective(change, current, 2);

        ChangeRendering.Revert(current, resolved).Should().Be("the quick brown fox");
    }

    [Fact(Timeout = 5000)]
    public void Revert_RelocatesThroughAnEditMadeSinceTheProjection()
    {
        var steps = new List<ChangeProjection.VersionStep>
        {
            Step(1, "baseline", "alpha bravo charlie"),
            Step(2, "alice", "alpha BRAVO charlie"),
        };
        var projectedAgainst = steps[^1].CleanText;
        var change = ChangeProjection.Project("doc", steps, projectedAgainst, 2).Should().ContainSingle().Subject;

        // Someone else appended a sentence AFTER the projection was taken.
        var live = projectedAgainst + " and more prose was added later.";
        var resolved = ChangeRendering.ResolveEffective(change, live, 3);

        ChangeRendering.Revert(live, resolved)
            .Should().Be("alpha bravo charlie and more prose was added later.");
    }

    [Fact(Timeout = 5000)]
    public void Revert_UnlocatableRange_Throws()
    {
        var change = new TrackedChange { EffectiveStart = -1, EffectiveEnd = -1 };

        Action act = () => ChangeRendering.Revert("anything", change);

        act.Should().Throw<InvalidOperationException>("a silent no-op would read to the user as a lost revert");
    }
}
