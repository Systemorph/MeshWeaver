using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="ChangeRendering"/> (capture / resolve / accept) and
/// <see cref="CollaborativeRenderer"/> (the inline diff view) for tracked changes.
/// </summary>
public class ChangeRenderingTest
{
    private static TrackedChange Insertion(int start, string newText, string anchor, long version = 1) => new()
    {
        MarkerId = "i1",
        ChangeType = TrackedChangeType.Insertion,
        Start = start,
        Length = 0,
        NewText = newText,
        AnchorText = anchor,
        Version = version,
        Status = TrackedChangeStatus.Pending
    };

    private static TrackedChange Deletion(int start, string original, string anchor, long version = 1) => new()
    {
        MarkerId = "d1",
        ChangeType = TrackedChangeType.Deletion,
        Start = start,
        Length = original.Length,
        OriginalText = original,
        AnchorText = anchor,
        Version = version,
        Status = TrackedChangeStatus.Pending
    };

    private static TrackedChange Replacement(int start, string original, string newText, string anchor, long version = 1) => new()
    {
        MarkerId = "r1",
        ChangeType = TrackedChangeType.Replacement,
        Start = start,
        Length = original.Length,
        OriginalText = original,
        NewText = newText,
        AnchorText = anchor,
        Version = version,
        Status = TrackedChangeStatus.Pending
    };

    // ---- Classify ----

    [Theory(Timeout = 5000)]
    [InlineData(null, "added", TrackedChangeType.Insertion)]
    [InlineData("", "added", TrackedChangeType.Insertion)]
    [InlineData("gone", null, TrackedChangeType.Deletion)]
    [InlineData("old", "new", TrackedChangeType.Replacement)]
    public void Classify_DerivesKindFromTextPair(string? deleted, string? inserted, TrackedChangeType expected)
    {
        ChangeRendering.Classify(deleted, inserted).Should().Be(expected);
    }

    // ---- ResolveEffective ----

    [Fact(Timeout = 5000)]
    public void ResolveEffective_SameVersion_KeepsCapturedRange()
    {
        const string doc = "The mesh stores nodes.";
        var change = Deletion(doc.IndexOf("stores", StringComparison.Ordinal), "stores", doc);

        var resolved = ChangeRendering.ResolveEffective(change, doc, 1);

        doc.Substring(resolved.EffectiveStart, resolved.EffectiveEnd - resolved.EffectiveStart).Should().Be("stores");
    }

    [Fact(Timeout = 5000)]
    public void ResolveEffective_DocumentAhead_RecomputesRange()
    {
        const string atV1 = "The mesh stores nodes.";
        var change = Deletion(atV1.IndexOf("stores", StringComparison.Ordinal), "stores", atV1);

        const string atV2 = "Intro added. The mesh stores nodes.";
        var resolved = ChangeRendering.ResolveEffective(change, atV2, 2);

        atV2.Substring(resolved.EffectiveStart, resolved.EffectiveEnd - resolved.EffectiveStart).Should().Be("stores");
    }

    [Fact(Timeout = 5000)]
    public void ResolveEffective_InsertionPoint_TracksThroughEdits()
    {
        const string atV1 = "alpha beta gamma";
        var insertAt = atV1.IndexOf("beta", StringComparison.Ordinal);
        var change = Insertion(insertAt, "INSERTED ", atV1);

        const string atV2 = "ZZ alpha beta gamma";
        var resolved = ChangeRendering.ResolveEffective(change, atV2, 2);

        // The insertion point should still be right before "beta".
        atV2.Substring(resolved.EffectiveStart).Should().StartWith("beta");
    }

    // ---- Apply (accept) ----

    [Fact(Timeout = 5000)]
    public void Apply_Insertion_InsertsNewText()
    {
        const string doc = "alpha gamma";
        var change = ChangeRendering.ResolveEffective(
            Insertion(doc.IndexOf("gamma", StringComparison.Ordinal), "beta ", doc), doc, 1);

        ChangeRendering.Apply(doc, change).Should().Be("alpha beta gamma");
    }

    [Fact(Timeout = 5000)]
    public void Apply_Deletion_RemovesRange()
    {
        const string doc = "alpha beta gamma";
        var change = ChangeRendering.ResolveEffective(
            Deletion(doc.IndexOf("beta ", StringComparison.Ordinal), "beta ", doc), doc, 1);

        ChangeRendering.Apply(doc, change).Should().Be("alpha gamma");
    }

    [Fact(Timeout = 5000)]
    public void Apply_Replacement_SwapsText()
    {
        const string doc = "the quick fox";
        var change = ChangeRendering.ResolveEffective(
            Replacement(doc.IndexOf("quick", StringComparison.Ordinal), "quick", "slow", doc), doc, 1);

        ChangeRendering.Apply(doc, change).Should().Be("the slow fox");
    }

    [Fact(Timeout = 5000)]
    public void Apply_AfterEditAbove_AppliesAtRelocatedRange()
    {
        const string atV1 = "the quick fox";
        var change = Replacement(atV1.IndexOf("quick", StringComparison.Ordinal), "quick", "slow", atV1);

        const string atV2 = "well, the quick fox";
        var resolved = ChangeRendering.ResolveEffective(change, atV2, 2);

        ChangeRendering.Apply(atV2, resolved).Should().Be("well, the slow fox");
    }

    [Fact(Timeout = 5000)]
    public void ApplyAll_AppliesEveryChange_RegardlessOfOrder()
    {
        const string doc = "one two three four";
        var changes = new[]
        {
            Deletion(doc.IndexOf("two ", StringComparison.Ordinal), "two ", doc),
            Replacement(doc.IndexOf("four", StringComparison.Ordinal), "four", "FOUR", doc),
            Insertion(0, "ZERO ", doc)
        };

        ChangeRendering.ApplyAll(doc, changes, 1).Should().Be("ZERO one three FOUR");
    }

    // ---- CollaborativeRenderer diff view ----

    [Fact(Timeout = 5000)]
    public void Decorate_Deletion_WrapsTrackDeleteSpan()
    {
        const string doc = "alpha beta gamma";
        var change = ChangeRendering.ResolveEffective(
            Deletion(doc.IndexOf("beta", StringComparison.Ordinal), "beta", doc), doc, 1);

        var decorated = CollaborativeRenderer.Decorate(doc, Array.Empty<Comment>(), new[] { change });

        decorated.Should().Contain("<span class=\"track-delete\" data-change-id=\"d1\">beta</span>");
    }

    [Fact(Timeout = 5000)]
    public void Decorate_Insertion_AddsTrackInsertSpanWithNewText()
    {
        const string doc = "alpha gamma";
        var change = ChangeRendering.ResolveEffective(
            Insertion(doc.IndexOf("gamma", StringComparison.Ordinal), "beta ", doc), doc, 1);

        var decorated = CollaborativeRenderer.Decorate(doc, Array.Empty<Comment>(), new[] { change });

        decorated.Should().Contain("<span class=\"track-insert\" data-change-id=\"i1\">beta </span>gamma");
    }

    [Fact(Timeout = 5000)]
    public void Decorate_Replacement_ShowsOldStruckAndNewAdded()
    {
        const string doc = "the quick fox";
        var change = ChangeRendering.ResolveEffective(
            Replacement(doc.IndexOf("quick", StringComparison.Ordinal), "quick", "slow", doc), doc, 1);

        var decorated = CollaborativeRenderer.Decorate(doc, Array.Empty<Comment>(), new[] { change });

        decorated.Should().Contain("<span class=\"track-delete\" data-change-id=\"r1\">quick</span><span class=\"track-insert\" data-change-id=\"r1\">slow</span>");
    }

    [Fact(Timeout = 5000)]
    public void Decorate_OnlyPendingChangesAreShown()
    {
        const string doc = "alpha beta gamma";
        var accepted = ChangeRendering.ResolveEffective(
            Deletion(doc.IndexOf("beta", StringComparison.Ordinal), "beta", doc), doc, 1)
            with { Status = TrackedChangeStatus.Accepted };

        var decorated = CollaborativeRenderer.Decorate(doc, Array.Empty<Comment>(), new[] { accepted });

        decorated.Should().Be(doc, "accepted/rejected changes are not shown in the diff view");
    }

    [Fact(Timeout = 5000)]
    public void Decorate_CommentAndChange_CoexistInOnePass()
    {
        const string doc = "the quick brown fox";
        var comment = CommentRendering.ResolveEffective(
            new Comment { MarkerId = "c1", HighlightedText = "brown", Start = doc.IndexOf("brown", StringComparison.Ordinal), Length = 5, AnchorText = doc, Version = 1 },
            doc, 1);
        var change = ChangeRendering.ResolveEffective(
            Deletion(doc.IndexOf("quick", StringComparison.Ordinal), "quick", doc), doc, 1);

        var decorated = CollaborativeRenderer.Decorate(doc, new[] { comment }, new[] { change });

        decorated.Should().Contain("<span class=\"track-delete\" data-change-id=\"d1\">quick</span>");
        decorated.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">brown</span>");
    }
}
