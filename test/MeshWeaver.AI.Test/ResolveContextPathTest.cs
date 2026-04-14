using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for <see cref="MeshOperations.ResolveContextPath"/>. Regression coverage for the
/// 2026-04-15 prod bug where <c>CollaborationPlugin.SuggestEdit</c> / <c>AddComment</c> ignored
/// the chat's context path — agents calling the tool with a relative path or bare display name
/// had the request routed to a non-existent grain (e.g. "Final Report – AI Readiness Assessment"),
/// and the edits never applied.
/// </summary>
public class ResolveContextPathTest
{
    [Theory]
    [InlineData("@/PartnerRe/AIConsulting/FinalReport", "@PartnerRe/AIConsulting/FinalReport")] // absolute @/ → keeps path
    [InlineData("/PartnerRe/AIConsulting/FinalReport", "@PartnerRe/AIConsulting/FinalReport")] // absolute / → rewrites to @
    [InlineData("@OrgA/Doc", "@OrgA/Doc")] // multi-segment already looks absolute → returned as-is
    [InlineData("@Doc/Architecture/content:file.svg", "@Doc/Architecture/content:file.svg")] // colon with slash before → absolute
    public void AbsolutePaths_AreReturnedUnchanged(string input, string expected)
    {
        var chat = new StubChat(new AgentContext { Context = "PartnerRe/AIConsulting" });
        MeshOperations.ResolveContextPath(chat, input).Should().Be(expected);
    }

    [Fact]
    public void RelativeBareName_IsPrefixedWithContextPath()
    {
        // This is the bug scenario: agent passes just "FinalReport" (or @FinalReport), expecting
        // the tool to find it under the current context. Before the fix this went straight to the
        // mesh as "FinalReport" and Orleans threw "Cannot activate grain FinalReport".
        var chat = new StubChat(new AgentContext { Context = "PartnerRe/AIConsulting" });

        MeshOperations.ResolveContextPath(chat, "FinalReport")
            .Should().Be("@PartnerRe/AIConsulting/FinalReport");
        MeshOperations.ResolveContextPath(chat, "@FinalReport")
            .Should().Be("@PartnerRe/AIConsulting/FinalReport");
    }

    [Fact]
    public void RelativeUnifiedPath_IsPrefixedWithContextPath()
    {
        // "content/report.docx" — UCR prefix path; relative to context.
        var chat = new StubChat(new AgentContext { Context = "PartnerRe/AIConsulting" });

        MeshOperations.ResolveContextPath(chat, "@content/report.docx")
            .Should().Be("@PartnerRe/AIConsulting/content/report.docx");
    }

    [Fact]
    public void RelativeColonPath_IsPrefixedWithContextPath()
    {
        // Legacy colon syntax: "content:file.md" — no slash before colon, so relative.
        var chat = new StubChat(new AgentContext { Context = "Doc/Architecture" });

        MeshOperations.ResolveContextPath(chat, "@content:icon.svg")
            .Should().Be("@Doc/Architecture/content:icon.svg");
    }

    [Fact]
    public void QuotedPath_IsUnwrappedBeforeResolving()
    {
        // Autocomplete wraps spaced paths in quotes: "@content/My File.md"
        var chat = new StubChat(new AgentContext { Context = "Doc/Architecture" });

        MeshOperations.ResolveContextPath(chat, "\"@content/My File.md\"")
            .Should().Be("@Doc/Architecture/content/My File.md");
    }

    [Fact]
    public void NoContext_RelativePath_ReturnsInputUnchanged()
    {
        var chat = new StubChat(context: null);

        MeshOperations.ResolveContextPath(chat, "FinalReport").Should().Be("FinalReport");
        MeshOperations.ResolveContextPath(chat, "@FinalReport").Should().Be("@FinalReport");
    }

    [Fact]
    public void NoContext_AbsolutePath_StillResolves()
    {
        var chat = new StubChat(context: null);

        MeshOperations.ResolveContextPath(chat, "@/OrgA/Doc").Should().Be("@OrgA/Doc");
    }

    [Fact]
    public void EmptyPath_ReturnsEmpty()
    {
        var chat = new StubChat(new AgentContext { Context = "OrgA" });
        MeshOperations.ResolveContextPath(chat, "").Should().Be("");
    }

    /// <summary>
    /// Minimal <see cref="IAgentChat"/> stub exposing only <see cref="IAgentChat.Context"/>.
    /// All other members throw — the method under test only reads Context.
    /// </summary>
    private sealed class StubChat : IAgentChat
    {
        public StubChat(AgentContext? context) => Context = context;

        public AgentContext? Context { get; }

        public void SetContext(AgentContext? applicationContext) => throw new NotImplementedException();
        public void SetSelectedAgent(string? agentName) => throw new NotImplementedException();
        public Task ResumeAsync(ChatConversation conversation) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync() => throw new NotImplementedException();
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void SetThreadId(string threadId) => throw new NotImplementedException();
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) => throw new NotImplementedException();
    }
}
