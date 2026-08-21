using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Hosting.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Presentation mode (issue #1803) — the PURE half: what "hidden" means, and the two writes that
/// produce it.
///
/// <para>The positive assertions here are the cheap ones. The load-bearing ones are the NEGATIVES,
/// because they are the whole design decision: a marking on its own hides nothing, a marking is one
/// user's and invisible to every other, and nothing in this feature is a permission. If the screen
/// ever starts gating a read it becomes a second access-control system that can disagree with the
/// real one — so those properties are pinned here, not left to review.</para>
/// </summary>
public class PresentationScreenTest
{
    private static PresentationScreen Active(params string[] marks)
        => PresentationScreen.For(true, marks);

    // ── What "hidden" means ────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkingAlone_HidesNothing_UntilTheModeIsOn()
    {
        // The complete undo #1803 asks for: flip the mode off and every mark is inert, with no
        // restore step and nothing to clean up.
        var off = PresentationScreen.For(active: false, ["Acme"]);

        off.Active.Should().BeFalse();
        off.MarkedPaths.Should().Contain("Acme", "the mark survives the mode being off — it is a standing preference");
        off.Hides("Acme").Should().BeFalse("a mark hides nothing while presentation mode is off");
        off.Filter(new[] { "Acme", "Northwind" }, p => p).Should().Equal("Acme", "Northwind");
    }

    [Fact]
    public void ActiveAndMarked_IsHidden()
    {
        Active("Acme").Hides("Acme").Should().BeTrue();
    }

    [Fact]
    public void MarkingASpace_HidesItsSubtree()
    {
        // The path IS the name: listing "Acme/Q3-Renewal" under Last edited would leak exactly what
        // marking "Acme" was meant to keep off the screen.
        var screen = Active("Acme");

        screen.Hides("Acme/Q3-Renewal").Should().BeTrue();
        screen.Hides("Acme/Q3-Renewal/Notes/Pricing").Should().BeTrue();
    }

    [Fact]
    public void ContainmentIsBySegment_NotByPrefix()
    {
        var screen = Active("Acme");

        screen.Hides("AcmeCorp").Should().BeFalse("a different space that merely starts with the same letters");
        screen.Hides("AcmeCorp/Deal").Should().BeFalse();
        screen.Hides("Northwind/Acme").Should().BeFalse("the mark is anchored at the root, not matched anywhere");
    }

    [Fact]
    public void PathsAreNormalized_CaseAndSlashes()
    {
        var screen = PresentationScreen.For(true, ["/Acme/", "  ", "", "Northwind"]);

        screen.MarkedPaths.Order(StringComparer.Ordinal).Should().Equal("Acme", "Northwind");
        screen.Hides("acme").Should().BeTrue();
        screen.Hides("/Acme").Should().BeTrue();
        screen.Hides("ACME/Deal").Should().BeTrue();
    }

    [Fact]
    public void BlankMarks_CannotBecomeAMatchEverythingRule()
    {
        // A hand-edited profile full of whitespace must not produce a screen that blanks the portal.
        var screen = PresentationScreen.For(true, ["", "   ", "/", "//"]);

        screen.MarkedPaths.Should().BeEmpty();
        screen.Hides("Acme").Should().BeFalse();
        screen.Hides("").Should().BeFalse();
    }

    [Fact]
    public void Off_IsTheNeutralScreen()
    {
        PresentationScreen.Off.Active.Should().BeFalse();
        PresentationScreen.Off.MarkedPaths.Should().BeEmpty();
        PresentationScreen.Off.Hides("Acme").Should().BeFalse();
        PresentationScreen.For(false, []).Should().BeSameAs(PresentationScreen.Off);
        PresentationScreen.From(null).Should().BeSameAs(PresentationScreen.Off);
    }

    [Fact]
    public void ModeOnWithNothingMarked_StaysObservable()
    {
        // The header indicator reads Active: a user who turns the mode on before marking anything
        // must still see it lit, so this may not collapse to Off.
        var screen = PresentationScreen.For(true, []);

        screen.Active.Should().BeTrue();
        screen.Hides("Acme").Should().BeFalse();
    }

    // ── Per-user isolation: the negative half ──────────────────────────────────────────────────

    [Fact]
    public void AScreenIsOneUsers_AnotherUserIsUnaffected()
    {
        var alice = new User { PresentationMode = true, HiddenPaths = ["Acme"] };
        // Same mode, different marks — Bob is presenting too, and Acme is not his to hide.
        var bob = new User { PresentationMode = true, HiddenPaths = ["Northwind"] };
        var carol = new User();

        PresentationScreen.From(alice).Hides("Acme").Should().BeTrue();
        PresentationScreen.From(bob).Hides("Acme").Should()
            .BeFalse("a marking lives on the marker's own profile and cannot reach another viewer");
        PresentationScreen.From(carol).Hides("Acme").Should().BeFalse();
        PresentationScreen.From(alice).Hides("Northwind").Should().BeFalse();
    }

    // ── Filtering ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_DropsHiddenItemsAndKeepsEverythingElse()
    {
        var nodes = new[]
        {
            MeshNode.FromPath("Acme"),
            MeshNode.FromPath("Acme/Q3-Renewal"),
            MeshNode.FromPath("Northwind"),
            MeshNode.FromPath("AcmeCorp"),
        };

        Active("Acme").Filter(nodes, n => n.Path).Select(n => n.Path)
            .Should().Equal("Northwind", "AcmeCorp");
    }

    [Fact]
    public void Filter_KeepsItemsThatAreNotNodes()
    {
        // A "~/" system area, a tag keyword, a slash command, an external URL: no mesh path, so
        // nothing to hide. The screen hides paths, not vocabulary.
        var items = new[] { "Acme", null, "https://example.org/Page" };

        Active("Acme").Filter(items, p => p is null or "https://example.org/Page" ? null : p)
            .Should().Equal([null, "https://example.org/Page"]);
    }

    [Fact]
    public void Retain_PreservesOrderAndOriginalSpelling()
    {
        // Pinned tiles feed this list straight into a query string, so the surviving entries must
        // come back exactly as the user wrote them.
        Active("Acme").Retain(["Northwind", "/Acme/", "Doc/Guide"])
            .Should().Equal("Northwind", "Doc/Guide");
        Active("Acme").Retain(null).Should().BeEmpty();
    }

    // ── The two writes ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyMark_AddsRemovesAndIsIdempotent()
    {
        var empty = new List<string>();

        var marked = PresentationPreference.ApplyMark(empty, "Acme", hide: true);
        marked.Should().Equal("Acme");

        // Idempotent: the SAME instance back, so a menu clicked twice writes nothing and mints no
        // node version.
        PresentationPreference.ApplyMark(marked, "/acme/", hide: true).Should().BeSameAs(marked);

        PresentationPreference.ApplyMark(marked, "ACME", hide: false).Should().BeEmpty();
        PresentationPreference.ApplyMark(marked, "Northwind", hide: false).Should().BeSameAs(marked);
        PresentationPreference.ApplyMark(marked, "  ", hide: true).Should().BeSameAs(marked);
    }

    [Fact]
    public void ApplyMark_LeavesTheOtherMarksInOrder()
    {
        var marks = new[] { "Acme", "Northwind", "Doc" };

        PresentationPreference.ApplyMark(marks, "Northwind", hide: false)
            .Should().Equal("Acme", "Doc");
        PresentationPreference.ApplyMark(marks, "Contoso", hide: true)
            .Should().Equal("Acme", "Northwind", "Doc", "Contoso");
    }

    [Fact]
    public void SetMode_FlipsTheModeAndTouchesNothingElse()
    {
        var user = new User
        {
            FullName = "Alice",
            TimeZoneId = "Europe/Zurich",
            PinnedPaths = ["Doc"],
            HiddenPaths = ["Acme"],
        };

        var on = PresentationPreference.SetMode(user, true);
        on.PresentationMode.Should().BeTrue();
        // The marks are untouched by the toggle.
        on.HiddenPaths.Should().Equal("Acme");
        on.PinnedPaths.Should().Equal("Doc");
        on.TimeZoneId.Should().Be("Europe/Zurich");
        on.FullName.Should().Be("Alice");

        PresentationPreference.SetMode(on, false).PresentationMode.Should().BeFalse();
        // A brand-new account, whose node exists but whose content has never been written.
        PresentationPreference.SetMode(null, true).PresentationMode.Should().BeTrue();
    }

    // ── The seam that resolves it ──────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyAPersonalViewerHasAScreen()
    {
        PresentationScreenExtensions.IsPersonalViewer("alice").Should().BeTrue();
        PresentationScreenExtensions.IsPersonalViewer(null).Should().BeFalse();
        PresentationScreenExtensions.IsPersonalViewer("").Should().BeFalse();
        PresentationScreenExtensions.IsPersonalViewer(WellKnownUsers.Anonymous).Should().BeFalse();
        PresentationScreenExtensions.IsPersonalViewer(WellKnownUsers.Public).Should().BeFalse();
        PresentationScreenExtensions.IsPersonalViewer(WellKnownUsers.System).Should().BeFalse();
    }

    [Fact]
    public void Project_ReadsTheProfileWhateverShapeTheContentArrivesIn()
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        var typed = MeshNode.FromPath("alice") with
        {
            NodeType = "User",
            Content = new User { PresentationMode = true, HiddenPaths = ["Acme"] }
        };

        PresentationScreenExtensions.Project(typed, options).Hides("Acme").Should().BeTrue();
        PresentationScreenExtensions.Project(null, options).Should().BeSameAs(PresentationScreen.Off);
    }

    [Fact]
    public void AFaultingProfileStream_HoldsTheLastKnownScreen_AndNeverWidensIt()
    {
        // 🚨 The leak this guards: resetting an ACTIVE screen to "nothing hidden" because a read
        // blipped would un-hide everything mid-presentation, silently. Holding the last value is
        // the fail-closed answer; refusing to emit at all would hang every surface waiting on it.
        var source = new Subject<PresentationScreen>();
        var seen = new List<PresentationScreen>();
        using var _ = PresentationScreenExtensions
            .LastKnownOnFault(source, "alice", logger: null)
            .Subscribe(seen.Add);

        source.OnNext(Active("Acme"));
        source.OnError(new InvalidOperationException("profile stream died"));

        seen.Should().HaveCount(2);
        seen[1].Hides("Acme").Should().BeTrue("the screen must survive the fault, not be widened by it");
    }

    [Fact]
    public void AFaultBeforeAnyValue_FallsBackToOff_RatherThanHanging()
    {
        var source = new Subject<PresentationScreen>();
        var seen = new List<PresentationScreen>();
        using var _ = PresentationScreenExtensions
            .LastKnownOnFault(source, "alice", logger: null)
            .Subscribe(seen.Add);

        source.OnError(new InvalidOperationException("profile stream died"));

        // Nothing was ever known, so there is nothing to hold — and a surface gated on the first
        // emission must not spin forever.
        seen.Should().ContainSingle().Which.Should().BeSameAs(PresentationScreen.Off);
    }

    [Fact]
    public void TwoSubscribersEachHoldTheirOwnLastValue()
    {
        // The latch is per-subscription (it lives inside Observable.Defer), never shared: two
        // circuits watching the same viewer must not see each other's state.
        var a = new Subject<PresentationScreen>();
        var b = new Subject<PresentationScreen>();
        var seenA = new List<PresentationScreen>();
        var seenB = new List<PresentationScreen>();
        using var subA = PresentationScreenExtensions.LastKnownOnFault(a, "alice", null).Subscribe(seenA.Add);
        using var subB = PresentationScreenExtensions.LastKnownOnFault(b, "bob", null).Subscribe(seenB.Add);

        a.OnNext(Active("Acme"));
        b.OnError(new InvalidOperationException("bob's profile stream died"));

        seenB.Should().ContainSingle().Which.Should().BeSameAs(PresentationScreen.Off);
    }

    [Fact]
    public void ASeededScreen_RendersEvenWhenTheProfileStreamNeverProduces()
    {
        // 🚨 The regression this pins is a WALL-CLOCK HANG, not a wrong value, and it has no failing
        // test to point at when it happens — the shard just runs out of clock. The node menu joins
        // the viewer's screen into a CombineLatest; that leg subscribes the VIEWER's own User node,
        // which need not exist. A stream that ERRORS is answered by LastKnownOnFault; a stream that
        // merely never produces is not, and an unseeded join then renders nothing, forever, outside
        // any method timeout.
        var never = Observable.Never<PresentationScreen>();
        var menuish = Observable.Return("items");

        var seen = new List<string>();
        using var _ = menuish
            .CombineLatest(never.Seeded(), (items, screen) => $"{items}/{screen.HidesAnything}")
            .Subscribe(seen.Add);

        // Without the seed this list is EMPTY — which is exactly what a hung shard looks like.
        seen.Should().Equal("items/False");
    }

    [Fact]
    public void ASeededScreen_StillDeliversTheRealScreenWhenItArrives()
    {
        // The seed must not swallow the answer: it is a floor, not a replacement.
        var source = new Subject<PresentationScreen>();
        var seen = new List<PresentationScreen>();
        using var _ = source.Seeded().Subscribe(seen.Add);

        seen.Should().ContainSingle().Which.Should().BeSameAs(PresentationScreen.Off);
        source.OnNext(Active("Acme"));

        seen.Should().HaveCount(2);
        seen[1].Hides("Acme").Should().BeTrue();
    }

    // ── Autocomplete projection ────────────────────────────────────────────────────────────────

    [Fact]
    public void Completions_AreScreenedByTheirPath()
    {
        var items = new[]
        {
            new AutocompleteItem("Acme", "@Acme/", Path: "Acme"),
            new AutocompleteItem("Q3-Renewal", "@Acme/Q3-Renewal/", Path: "Acme/Q3-Renewal"),
            new AutocompleteItem("Northwind", "@Northwind/", Path: "Northwind"),
            // No Path set — the absolute insert text is the fallback the orchestrator derives from.
            new AutocompleteItem("Deal", "@Acme/Deal/"),
            // Not a node at all: a tag keyword keeps working while the mode is on.
            new AutocompleteItem("content/", "content/", Kind: AutocompleteKind.Command),
        };

        var painted = Active("Acme")
            .Filter(items, ChatCompletionOrchestrator.CompletionPath)
            .Select(i => i.Label)
            .ToArray();

        painted.Should().Equal("Northwind", "content/");
    }

    [Fact]
    public void AScreenWithMarksButTheModeOff_HidesNothingAndIsNotOff()
    {
        // 🚨 The two are DIFFERENT values, and conflating them is a real defect (Copilot review on
        // #1991): a viewer who marked things and then turned the mode off holds a screen that is
        // not `Off` by reference and yet must hide nothing. A caller whose fast-path tests
        // `== Off` sends them down the filtering branch — where Filter is correctly a no-op, but
        // anything ELSE that branch does is not. In the completion pipeline that "anything else"
        // was dropping a legitimately empty category for someone who is not presenting.
        var marksOnly = PresentationScreen.For(false, ["Acme"]);

        marksOnly.Should().NotBeSameAs(PresentationScreen.Off);
        marksOnly.HidesAnything.Should().BeFalse();
        marksOnly.Hides("Acme").Should().BeFalse();

        // …and the other direction: the mode on with nothing marked also hides nothing, while
        // staying observable so the header indicator can read Active.
        var modeOnly = PresentationScreen.For(true, []);
        modeOnly.Active.Should().BeTrue();
        modeOnly.HidesAnything.Should().BeFalse();

        Active("Acme").HidesAnything.Should().BeTrue();
        PresentationScreen.Off.HidesAnything.Should().BeFalse();
    }

    [Fact]
    public void Completions_AreUntouchedWhenTheScreenHidesNothing()
    {
        // The batch list must come back byte-for-byte for a non-presenting viewer — INCLUDING an
        // empty batch, which the presenting path deliberately drops.
        var batches = new[]
        {
            new CompletionBatch("Nearby", 0, [new AutocompleteItem("Acme", "@Acme/", Path: "Acme")]),
            new CompletionBatch("Partitions", 2000, []),
        };

        foreach (var screen in new[]
                 {
                     PresentationScreen.Off,
                     PresentationScreen.For(false, ["Acme"]),
                     PresentationScreen.For(true, []),
                 })
        {
            var painted = ChatCompletionOrchestrator
                .Screened(batches.ToObservable(), screen)
                .ToEnumerable().ToArray();

            painted.Select(b => b.Category).Should().Equal("Nearby", "Partitions");
            painted[1].Items.Should().BeEmpty(
                "an empty category is only dropped while the viewer is actually presenting");
        }
    }

    [Fact]
    public void Completions_DropAnEmptiedCategory_OnlyWhilePresenting()
    {
        // The other half of the same rule: with the screen genuinely up, a category the screen
        // emptied is removed rather than left as a heading with nothing under it.
        var batches = new[]
        {
            new CompletionBatch("Nearby", 0, [new AutocompleteItem("Acme", "@Acme/", Path: "Acme")]),
            new CompletionBatch("Elsewhere", 10, [new AutocompleteItem("NW", "@Northwind/", Path: "Northwind")]),
        };

        var painted = ChatCompletionOrchestrator
            .Screened(batches.ToObservable(), Active("Acme"))
            .ToEnumerable().ToArray();

        painted.Select(b => b.Category).Should().Equal("Elsewhere");
    }

    [Fact]
    public void CompletionPath_PrefersTheDeclaredPath_ThenTheAbsoluteInsertText()
    {
        ChatCompletionOrchestrator.CompletionPath(
            new AutocompleteItem("x", "@Other/", Path: "Acme")).Should().Be("Acme");
        ChatCompletionOrchestrator.CompletionPath(
            new AutocompleteItem("x", "@Acme/Deal/")).Should().Be("Acme/Deal");
        ChatCompletionOrchestrator.CompletionPath(
            new AutocompleteItem("x", "content/")).Should().BeNull();
        ChatCompletionOrchestrator.CompletionPath(
            new AutocompleteItem("x", "@")).Should().BeNull();
    }
}
