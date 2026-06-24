using System.ComponentModel;
using System.Threading;
using MeshWeaver.AI.Attributes;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the <see cref="HiddenToolAttribute"/> contract that <c>AgentChatClient</c>
/// relies on to suppress internal-plumbing tools (e.g. <c>check_inbox</c>) from the
/// chat UI and tool-activity logs:
/// <list type="bullet">
///   <item>A method annotated <c>[HiddenTool]</c> → <see cref="HiddenToolAttribute.IsHidden"/>
///         is <c>true</c> for the <see cref="AIFunction"/> built from it.</item>
///   <item>An un-annotated method → <c>false</c>.</item>
///   <item>The <b>lambda</b> shape <c>check_inbox</c> actually uses — the attribute is on
///         a lambda passed to <see cref="AIFunctionFactory"/> — must still round-trip
///         through <see cref="AIFunction.UnderlyingMethod"/>.</item>
/// </list>
/// </summary>
public class HiddenToolAttributeTest
{
    [Fact]
    public void AnnotatedMethod_IsHidden()
    {
        var fn = AIFunctionFactory.Create(HiddenToolMethod);
        HiddenToolAttribute.IsHidden(fn).Should().BeTrue();
    }

    [Fact]
    public void PlainMethod_IsNotHidden()
    {
        var fn = AIFunctionFactory.Create(VisibleToolMethod);
        HiddenToolAttribute.IsHidden(fn).Should().BeFalse();
    }

    /// <summary>
    /// The exact shape <c>InboxTool.CreateCheckInboxTool</c> uses: the marker sits on a
    /// lambda (so it can close over the hub) rather than a named method. C# emits lambda
    /// attributes onto the generated method, and <see cref="AIFunctionFactory"/> exposes
    /// that method as <see cref="AIFunction.UnderlyingMethod"/> — so the filter sees it.
    /// </summary>
    [Fact]
    public void AnnotatedLambda_IsHidden()
    {
        var fn = AIFunctionFactory.Create(
            [HiddenTool] (CancellationToken ct) => "ok",
            name: "fake_check_inbox",
            description: "test");
        HiddenToolAttribute.IsHidden(fn).Should().BeTrue();
    }

    [Description("Hidden test tool")]
    [HiddenTool]
    private static string HiddenToolMethod() => "ok";

    [Description("Visible test tool")]
    private static string VisibleToolMethod() => "ok";
}
