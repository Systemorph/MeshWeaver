using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit coverage for <see cref="AgentChatClient.TruncateToContextBudget"/> — the per-round history cap
/// that keeps input tokens (and local-model compute/heat) from growing quadratically with thread length.
/// </summary>
public class TruncateToContextBudgetTest
{
    private static ChatMessage Sys(string t) => new(ChatRole.System, t);
    private static ChatMessage User(string t) => new(ChatRole.User, t);
    private static ChatMessage Asst(string t) => new(ChatRole.Assistant, t);
    private static ChatMessage Tool(string t) => new(ChatRole.Tool, t);
    private static int Size(ChatMessage m) => (m.Text?.Length ?? 0) + 64;

    [Fact]
    public void UnderBudget_ReturnsSameInstance()
    {
        var msgs = new List<ChatMessage> { Sys("s"), User("hi"), Asst("hello") };
        Assert.Same(msgs, AgentChatClient.TruncateToContextBudget(msgs, 10_000));
    }

    [Fact]
    public void OverBudget_KeepsSystemAndNewest_DropsOldest()
    {
        var msgs = new List<ChatMessage> { Sys("SYS") };
        for (var i = 0; i < 100; i++)
            msgs.Add(User(new string('x', 100) + $"#{i}"));

        var result = AgentChatClient.TruncateToContextBudget(msgs, 1000);

        Assert.Equal(ChatRole.System, result[0].Role);          // system kept, first
        Assert.Contains(result, m => m.Text == "SYS");
        Assert.True(result.Count < msgs.Count);                  // dropped some
        Assert.Contains(result, m => m.Text!.EndsWith("#99"));   // newest kept
        Assert.DoesNotContain(result, m => m.Text!.EndsWith("#0")); // oldest dropped
        Assert.True(result.Sum(Size) <= 1000 + 200, "kept set is ~within the budget");
        // Order preserved (ascending index among the kept #N).
        var kept = result.Where(m => m.Text!.Contains('#')).Select(m => int.Parse(m.Text!.Split('#')[1])).ToList();
        Assert.Equal(kept.OrderBy(x => x).ToList(), kept);
    }

    [Fact]
    public void AlwaysKeepsNewest_EvenIfItAloneExceedsBudget()
    {
        var huge = new string('y', 5000);
        var msgs = new List<ChatMessage> { Sys("s"), User("old"), User(huge) };
        var result = AgentChatClient.TruncateToContextBudget(msgs, 100);
        Assert.Contains(result, m => m.Text == huge);
    }

    [Fact]
    public void DropsLeadingOrphanedToolResult()
    {
        // Budget keeps ~[Tool result, User]; the Tool result's FunctionCall (the Assistant before it)
        // was dropped as older, so the leading orphan tool result must be dropped too.
        var msgs = new List<ChatMessage>
        {
            Sys("s"),
            User(new string('a', 200)),   // oldest — dropped
            Asst(new string('b', 200)),   // the tool CALL — dropped as older
            Tool(new string('t', 50)),    // orphan result — must be dropped from the window start
            User(new string('u', 50)),    // newest — kept
        };
        // sys(65) + tool(114) + user(114) = 293 → exactly fits Tool+User, forcing Tool to lead the window.
        var result = AgentChatClient.TruncateToContextBudget(msgs, 293);

        var nonSystem = result.Where(m => m.Role != ChatRole.System).ToList();
        Assert.DoesNotContain(nonSystem, m => m.Role == ChatRole.Tool);
        Assert.Contains(nonSystem, m => m.Role == ChatRole.User && m.Text!.Length == 50);
    }
}
