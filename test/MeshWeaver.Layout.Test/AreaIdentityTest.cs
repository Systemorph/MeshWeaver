using System.Linq;
using System.Text.Json;
using MeshWeaver.Blazor;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the CLIENT-side identity rules the renderer keys container children and embedded layout
/// areas by (#732 / #733). The renderer itself has no test host here (no bunit), so these cover the
/// identity functions the razor components key on — see the PR body for that gap.
/// </summary>
public class AreaIdentityTest
{
    private static StackControl Stack(params (UiControl View, string Area)[] children)
        => children.Aggregate(Controls.Stack, (stack, c) => stack.WithView(c.View, c.Area));

    // ─── #732: container children are identified by AREA NAME, never by position ───

    [Fact]
    public void InsertingAChildAtTheFront_LeavesTheExistingChildsKeyUnchanged()
    {
        var before = Stack((Controls.Html("all-steps"), "AllSteps"));
        var after = Stack((Controls.Html("back"), "Back"), (Controls.Html("all-steps"), "AllSteps"));

        var beforeKeys = ((IContainerControl)before).ResolveChildren("Main").ToArray();
        var afterKeys = ((IContainerControl)after).ResolveChildren("Main").ToArray();

        beforeKeys.Select(c => c.Key).Should().Equal("Main/AllSteps");
        afterKeys.Select(c => c.Key).Should().Equal("Main/Back", "Main/AllSteps");

        // The surviving child keeps its identity even though its INDEX moved from 0 to 1 — that is
        // what stops the renderer from handing the retained "AllSteps" component the inserted "Back"
        // child (the duplicated-trailing-child / dropped-insert of #732).
        afterKeys[1].Key.Should().Be(beforeKeys[0].Key);
        afterKeys[0].Key.Should().NotBe(beforeKeys[0].Key);
    }

    [Fact]
    public void ReplacingTheChildSet_SharesNoKeyWithThePreviousSet()
    {
        var before = Stack((Controls.Html("prose"), "Prose"));
        var after = Stack(
            (Controls.Html("step"), "Step"),
            (Controls.Html("rail"), "Rail"),
            (Controls.Html("stage"), "Stage"));

        var beforeKeys = ((IContainerControl)before).ResolveChildren("Main").Select(c => c.Key).ToArray();
        var afterKeys = ((IContainerControl)after).ResolveChildren("Main").Select(c => c.Key).ToArray();

        afterKeys.Should().Equal("Main/Step", "Main/Rail", "Main/Stage");
        afterKeys.Intersect(beforeKeys).Should().BeEmpty(
            "a wholly different child set must unmount every previous child rather than re-parameterise it");
    }

    [Fact]
    public void ChildrenResolveTheirAbsoluteArea_AndKeysMatchIt()
    {
        var children = ((IContainerControl)Stack(
                (Controls.Html("a"), "A"),
                (Controls.Html("b"), "B")))
            .ResolveChildren("Main")
            .ToArray();

        children.Select(c => c.ResolvedArea).Should().Equal("Main/A", "Main/B");
        children.Select(c => c.Key).Should().Equal(children.Select(c => c.ResolvedArea));
    }

    [Fact]
    public void PrefixSharingSiblings_GetDistinctKeys()
    {
        var children = ((IContainerControl)Stack(
                (Controls.Html("a"), "Step"),
                (Controls.Html("b"), "Step2")))
            .ResolveChildren("Main")
            .ToArray();

        children.Select(c => c.Key).Should().Equal("Main/Step", "Main/Step2");
    }

    [Fact]
    public void DuplicateChildAreaIds_StillProduceDistinctKeys()
    {
        // Degenerate but must never throw in the renderer: sibling key collisions blank the page.
        var children = ((IContainerControl)Stack(
                (Controls.Html("a"), "Same"),
                (Controls.Html("b"), "Same")))
            .ResolveChildren("Main")
            .ToArray();

        children.Select(c => c.Key).Distinct().Should().HaveCount(2);
        children[0].Key.Should().Be("Main/Same");
    }

    // ─── #733: an embedded layout area is identified by (address, reference) ───

    [Fact]
    public void ChangingOnlyTheReference_ChangesTheStreamIdentity()
    {
        var address = new Address("host", "MTPL2027");
        var structure = new LayoutAreaControl(address, new LayoutAreaReference("Structure"));
        var economics = new LayoutAreaControl(address, new LayoutAreaReference("Economics"));

        economics.GetStreamIdentity().Should().NotBe(structure.GetStreamIdentity(),
            "a reference-only change addresses a DIFFERENT area stream, so the renderer must remount "
            + "rather than hot-swap the live stream under the retained component (#733)");
    }

    [Fact]
    public void ChangingOnlyTheReferenceId_ChangesTheStreamIdentity()
    {
        var address = new Address("host", "MTPL2027");
        var first = new LayoutAreaControl(address, new LayoutAreaReference("Structure") { Id = "v1" });
        var second = new LayoutAreaControl(address, new LayoutAreaReference("Structure") { Id = "v2" });

        second.GetStreamIdentity().Should().NotBe(first.GetStreamIdentity());
    }

    [Fact]
    public void ChangingOnlyTheAddress_ChangesTheStreamIdentity()
    {
        var reference = new LayoutAreaReference("Structure");
        var first = new LayoutAreaControl(new Address("host", "MTPL2027"), reference);
        var second = new LayoutAreaControl(new Address("host", "MTPL2028"), reference);

        second.GetStreamIdentity().Should().NotBe(first.GetStreamIdentity());
    }

    [Fact]
    public void AJsonRoundTrippedEmbed_KeepsItsStreamIdentity()
    {
        // Address and Reference.Id are `object`s that arrive as JsonElement on the client. If the
        // identity flapped between the two representations, the embedded area would remount on EVERY
        // render — a storm, not a fix.
        var built = new LayoutAreaControl(
            new Address("host", "MTPL2027"),
            new LayoutAreaReference("Structure") { Id = "v1" });
        var deserialized = new LayoutAreaControl(
            JsonSerializer.Deserialize<JsonElement>("\"host/MTPL2027\""),
            new LayoutAreaReference("Structure") { Id = JsonSerializer.Deserialize<JsonElement>("\"v1\"") });

        deserialized.GetStreamIdentity().Should().Be(built.GetStreamIdentity());
    }

    [Fact]
    public void AnUnchangedEmbed_KeepsItsStreamIdentity()
    {
        var address = new Address("host", "MTPL2027");
        var first = new LayoutAreaControl(address, new LayoutAreaReference("Structure"));
        // A re-render produces a NEW instance carrying display-only differences; the identity — and
        // therefore the mounted stream and its subtree — must survive it.
        var second = new LayoutAreaControl(address, new LayoutAreaReference("Structure"))
            .WithProgressMessage("loading…");

        second.GetStreamIdentity().Should().Be(first.GetStreamIdentity());
    }
}
