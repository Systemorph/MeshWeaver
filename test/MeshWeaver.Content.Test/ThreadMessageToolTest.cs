using System.Linq;
using MeshWeaver.AI.Plugins;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// The <c>submit_message</c> tool's REFUSAL contract — the half a model actually collides with.
///
/// <para>🚨 Every one of these inputs used to end a round rather than answer it. A tool that
/// throws does not produce a tool result the model can react to: the round aborts, and the user
/// sees a failed answer instead of "that path is not valid". In particular <c>/</c>, <c>@/</c> and
/// <c>///</c> all pass a whitespace check and then normalise to the empty string, where
/// <c>MeshNode.FromPath("")</c> throws <see cref="System.ArgumentException"/> — which is why the
/// validation runs on the NORMALISED value, not the raw argument.</para>
///
/// <para>The delivery half rides <c>HubThreadExtensions.SubmitMessage</c> (covered by the thread
/// tests) and its far-side handler is the per-thread submission watcher, so what is worth pinning
/// here is the boundary this tool adds: what it refuses, and that it refuses by ANSWERING.</para>
/// </summary>
public class ThreadMessageToolTest
{
    // hub/chat stay null on purpose: every case below refuses BEFORE either is touched, which is
    // itself part of the contract — a malformed path must never reach the hub or the router.
    private static AIFunction Tool() =>
        (AIFunction)ThreadMessageTool.Create(hub: null!, chat: null!);

    private static string Invoke(string? threadPath, string? text)
    {
        var result = Tool().InvokeAsync(new AIFunctionArguments
        {
            ["threadPath"] = threadPath!,
            ["text"] = text!,
        }).AsTask().GetAwaiter().GetResult();
        return result?.ToString() ?? string.Empty;
    }

    [Fact]
    public void The_tool_is_named_and_described_for_the_model()
    {
        var tool = Tool();
        tool.Name.Should().Be("submit_message");
        tool.Description.Should().Contain("ANOTHER thread",
            "the description has to stop an agent using this for its own conversation");
        tool.Description.Should().Contain("send_to_sub_thread",
            "it must point at the right tool for a sub-thread it dispatched itself");
    }

    [Theory]
    [InlineData("/")]        // normalises to empty — MeshNode.FromPath would throw
    [InlineData("@/")]
    [InlineData("///")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_path_that_normalises_to_nothing_is_ANSWERED_not_thrown(string? path)
    {
        // The assertion is as much that this returns at all as what it returns: before the
        // normalised-value check, three of these aborted the round with ArgumentException.
        var answer = Invoke(path, "hello");

        answer.Should().Contain("submit_message requires");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_text_is_refused_without_sending(string? text)
    {
        Invoke("alice/_Thread/review", text)
            .Should().Contain("non-empty text");
    }

    [Fact]
    public void An_ownerless_top_level_thread_path_is_refused_with_the_reason()
    {
        // A bare `_Thread/{id}` has no partition and no per-node hub; submitting to it would
        // NotFound-storm the router. Saying so beats an agent retrying the same bad path.
        var answer = Invoke("_Thread/orphan", "hello");

        answer.Should().Contain("not a valid thread path");
        answer.Should().Contain("{owner}/_Thread/{id}",
            "the refusal has to teach the shape, or the model guesses again");
    }
}
