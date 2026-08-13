using System.Text.Json;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the boundary between the two placeholder frames a layout area can serve. They look alike
/// to a human and mean opposite things to a consumer: "Area not found" is a VERDICT (nothing will
/// ever render here — give up and say so), the compile-progress page is a PROMISE (the instance's
/// NodeType is still building — keep waiting). Collapsing them either way is a real defect:
/// treating the promise as a verdict is issue #1411; treating the verdict as a promise turns a
/// clear answer into a wait that never ends.
/// </summary>
public class AreaFrameClassifierTest
{
    private static MarkdownControl NotFoundFrame() =>
        new("**Area not found**\n\nNo renderer is registered for area `KeyMetrics` on hub `A/B`.")
        {
            Id = AreaFrameClassifier.AreaNotFoundId
        };

    private static StackControl CompileProgressFrame() =>
        Controls.Stack.WithId(AreaFrameClassifier.CompileProgressId);

    [Fact]
    public void NotFoundFrame_IsNotFound_AndNotTransient()
    {
        var frame = NotFoundFrame();
        AreaFrameClassifier.IsAreaNotFound(frame).Should().BeTrue();
        AreaFrameClassifier.IsCompileProgress(frame).Should().BeFalse();
        AreaFrameClassifier.IsTransientFrame(frame).Should().BeFalse(
            "a missing area is a verdict — nothing is going to replace it");
    }

    [Fact]
    public void CompileProgressFrame_IsTransient_AndNotNotFound()
    {
        var frame = CompileProgressFrame();
        AreaFrameClassifier.IsCompileProgress(frame).Should().BeTrue();
        AreaFrameClassifier.IsTransientFrame(frame).Should().BeTrue();
        AreaFrameClassifier.IsAreaNotFound(frame).Should().BeFalse(
            "a page that is still building must never be reported as a page that does not exist");
    }

    /// <summary>
    /// The redirect the compile-progress surface emits the instant the build settles is just as
    /// much "not the content" as the progress page itself — a waiter that accepted it would latch
    /// a navigation instruction as the area's control.
    /// </summary>
    [Fact]
    public void Redirect_IsTransient()
    {
        AreaFrameClassifier.IsTransientFrame(Controls.Redirect("/Some/Where")).Should().BeTrue();
        AreaFrameClassifier.IsAreaNotFound(Controls.Redirect("/Some/Where")).Should().BeFalse();
    }

    /// <summary>
    /// 🚨 The trap that makes the marker worth a test. <see cref="UiControl.Id"/> is
    /// <c>object?</c>, so a frame that came back over the sync stream carries a
    /// <see cref="JsonElement"/> there, not a <see cref="string"/> — and the client side is the
    /// ONLY place these predicates are consulted. A CLR type test would answer "no" for every
    /// real frame while passing every unit test built from freshly-constructed controls.
    /// </summary>
    [Fact]
    public void FrameIdSurvivesTheWire_WhereIdIsAJsonElementNotAString()
    {
        var wireId = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(AreaFrameClassifier.CompileProgressId));
        wireId.Should().NotBeOfType<string>();

        var frame = Controls.Stack.WithId(wireId);
        AreaFrameClassifier.IsCompileProgress(frame).Should().BeTrue();
        AreaFrameClassifier.IsTransientFrame(frame).Should().BeTrue();
    }

    /// <summary>
    /// The markdown fallback: a not-found frame whose id did not survive (an older peer, a control
    /// rebuilt from partial JSON) is still recognised, so the fix cannot regress a consumer that
    /// used to match the banner.
    /// </summary>
    [Fact]
    public void NotFoundFrameWithoutItsId_IsStillRecognised()
        => AreaFrameClassifier.IsAreaNotFound(
                new MarkdownControl("**Area not found**\n\nNo renderer is registered for area `X`."))
            .Should().BeTrue();

    [Fact]
    public void OrdinaryContent_IsNeither()
    {
        var content = Controls.Markdown("### Total Premium\n\n1,234");
        AreaFrameClassifier.IsAreaNotFound(content).Should().BeFalse();
        AreaFrameClassifier.IsCompileProgress(content).Should().BeFalse();
        AreaFrameClassifier.IsTransientFrame(content).Should().BeFalse();

        AreaFrameClassifier.IsAreaNotFound(null).Should().BeFalse();
        AreaFrameClassifier.IsCompileProgress(null).Should().BeFalse();
        AreaFrameClassifier.IsTransientFrame(null).Should().BeFalse();
    }
}
