using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for <see cref="MeshOperations.ResolveContextPath"/>. Regression coverage for the
/// 2026-04-15 prod bug where <c>CollaborationPlugin.SuggestEdit</c> / <c>AddComment</c> ignored
/// the chat's context path â€” agents calling the tool with a relative path or bare display name
/// had the request routed to a non-existent grain (e.g. "Final Report â€“ AI Readiness Assessment"),
/// and the edits never applied.
/// </summary>
public class ResolveContextPathTest
{
    /// <summary>Absolute paths (leading <c>@/</c> or multi-segment <c>@</c>) are returned unchanged.</summary>
    [Theory]
    [InlineData("@/Acme/AIConsulting/FinalReport", "@Acme/AIConsulting/FinalReport")] // absolute @/ â†’ keeps path
    [InlineData("/Acme/AIConsulting/FinalReport", "@Acme/AIConsulting/FinalReport")] // absolute / â†’ rewrites to @
    [InlineData("@OrgA/Doc", "@OrgA/Doc")] // multi-segment already looks absolute â†’ returned as-is
    [InlineData("@Doc/Architecture/content:file.svg", "@Doc/Architecture/content:file.svg")] // colon with slash before â†’ absolute
    public void AbsolutePaths_AreReturnedUnchanged(string input, string expected)
    {
        var chat = new StubChat(new AgentContext { Context = "Acme/AIConsulting" });
        MeshOperations.ResolveContextPath(chat, input).Should().Be(expected);
    }

    /// <summary>Relative bare names are prefixed with the chat's context path before routing.</summary>
    [Fact]
    public void RelativeBareName_IsPrefixedWithContextPath()
    {
        // This is the bug scenario: agent passes just "FinalReport" (or @FinalReport), expecting
        // the tool to find it under the current context. Before the fix this went straight to the
        // mesh as "FinalReport" and Orleans threw "Cannot activate grain FinalReport".
        var chat = new StubChat(new AgentContext { Context = "Acme/AIConsulting" });

        MeshOperations.ResolveContextPath(chat, "FinalReport")
            .Should().Be("@Acme/AIConsulting/FinalReport");
        MeshOperations.ResolveContextPath(chat, "@FinalReport")
            .Should().Be("@Acme/AIConsulting/FinalReport");
    }

    /// <summary>Relative UCR prefix paths (e.g. <c>content/file</c>) resolve against the context.</summary>
    [Fact]
    public void RelativeUnifiedPath_IsPrefixedWithContextPath()
    {
        // "content/report.docx" â€” UCR prefix path; relative to context.
        var chat = new StubChat(new AgentContext { Context = "Acme/AIConsulting" });

        MeshOperations.ResolveContextPath(chat, "@content/report.docx")
            .Should().Be("@Acme/AIConsulting/content/report.docx");
    }

    /// <summary>Legacy colon-syntax relative paths (e.g. <c>content:file</c>) resolve against the context.</summary>
    [Fact]
    public void RelativeColonPath_IsPrefixedWithContextPath()
    {
        // Legacy colon syntax: "content:file.md" â€” no slash before colon, so relative.
        var chat = new StubChat(new AgentContext { Context = "Doc/Architecture" });

        MeshOperations.ResolveContextPath(chat, "@content:icon.svg")
            .Should().Be("@Doc/Architecture/content:icon.svg");
    }

    /// <summary>Quoted paths from autocomplete are unwrapped before context resolution.</summary>
    [Fact]
    public void QuotedPath_IsUnwrappedBeforeResolving()
    {
        // Autocomplete wraps spaced paths in quotes: "@content/My File.md"
        var chat = new StubChat(new AgentContext { Context = "Doc/Architecture" });

        MeshOperations.ResolveContextPath(chat, "\"@content/My File.md\"")
            .Should().Be("@Doc/Architecture/content/My File.md");
    }

    /// <summary>With no chat context, a relative path is returned verbatim (nothing to prefix).</summary>
    [Fact]
    public void NoContext_RelativePath_ReturnsInputUnchanged()
    {
        var chat = new StubChat(context: null);

        MeshOperations.ResolveContextPath(chat, "FinalReport").Should().Be("FinalReport");
        MeshOperations.ResolveContextPath(chat, "@FinalReport").Should().Be("@FinalReport");
    }

    /// <summary>Absolute paths still resolve (losing the leading slash) even without a chat context.</summary>
    [Fact]
    public void NoContext_AbsolutePath_StillResolves()
    {
        var chat = new StubChat(context: null);

        MeshOperations.ResolveContextPath(chat, "@/OrgA/Doc").Should().Be("@OrgA/Doc");
    }

    /// <summary>An empty input produces an empty output regardless of context.</summary>
    [Fact]
    public void EmptyPath_ReturnsEmpty()
    {
        var chat = new StubChat(new AgentContext { Context = "OrgA" });
        MeshOperations.ResolveContextPath(chat, "").Should().Be("");
    }

    /// <summary>
    /// 🚨 #1469. Asking for THE CONTEXT ITSELF must resolve to the context — not to a child of the
    /// context named after the context.
    ///
    /// <para>The prod signature was <c>No node found at 'felice.buergi/felice.buergi'. Closest
    /// ancestor is 'felice.buergi' (remainder='felice.buergi')</c>, three times in 20 hours and for
    /// exactly one account. The chat's context chip is shipped to the agent as an attachment
    /// (<c>ThreadChatView</c> → <c>AgentChatClient</c> → <c>MeshPlugin.Get("@{path}")</c> → here),
    /// and the relative/absolute decision above keys on a <c>/</c>: a token WITHOUT one is treated
    /// as relative. A single-segment mesh path is by definition a partition ROOT, so a user chatting
    /// from their own home page had context == attachment == <c>{userId}</c> and the prepend
    /// produced <c>{userId}/{userId}</c> — a node that cannot exist. Anyone anchored on a sub-node
    /// (<c>Acme/AIConsulting/…</c>) takes the "multi-segment ⇒ already absolute" exit and never
    /// doubled, which is exactly the observed scope. Nothing about it needs the dot in the id.</para>
    /// </summary>
    [Theory]
    [InlineData("felice.buergi", "@felice.buergi")]   // as AgentChatClient ships the context attachment
    [InlineData("felice.buergi", "felice.buergi")]    // as a model would copy it out of the prompt
    [InlineData("rbuergi", "@rbuergi")]               // no dot required
    [InlineData("OrgA", "@OrgA")]
    public void ContextEqualsPath_ResolvesToTheContext_NotToAChildOfItself(
        string contextPath, string input)
    {
        var chat = new StubChat(new AgentContext { Context = contextPath });

        MeshOperations.ResolveContextPath(chat, input)
            .Should().Be("@" + contextPath,
                "asking for the context node itself must resolve to that node, not to a "
                + "non-existent child of it bearing the same name (#1469)");
    }

    /// <summary>
    /// A path that already begins with the context at a SEGMENT boundary is already absolute —
    /// prepending would double the shared prefix. The boundary matters: a sibling whose name merely
    /// starts with the same characters is still relative.
    /// </summary>
    [Theory]
    [InlineData("Acme/AIConsulting", "@Acme/AIConsulting/FinalReport", "@Acme/AIConsulting/FinalReport")]
    [InlineData("Doc/Architecture", "@Doc/Architecture/content/file.md", "@Doc/Architecture/content/file.md")]
    [InlineData("OrgA", "@OrgAArchive", "@OrgA/OrgAArchive")] // NOT a segment match — stays relative
    public void SelfPrefixedPath_IsNotDoubled_ButOnlyOnASegmentBoundary(
        string contextPath, string input, string expected)
    {
        var chat = new StubChat(new AgentContext { Context = contextPath });
        MeshOperations.ResolveContextPath(chat, input).Should().Be(expected);
    }

    /// <summary>
    /// Minimal <see cref="IAgentChat"/> stub exposing only <see cref="IAgentChat.Context"/>.
    /// All other members throw â€” the method under test only reads Context.
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
