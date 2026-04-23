using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression coverage for the 2026-04-14 prod incident where an agent called
/// <c>SuggestEdit</c> / <c>AddComment</c> with a target that did not resolve to any
/// registered hub. The fire-and-forget <c>hub.Post</c> triggered a silent
/// <c>"Cannot activate grain &lt;name&gt;"</c> exception in Orleans, the user got no
/// feedback, and the edit quietly dropped.
///
/// After the fix, <see cref="MeshWeaver.AI.Plugins.CollaborationPlugin"/> uses the
/// truly-async Post + RegisterCallback + TCS pattern (see
/// <c>Doc/Architecture/AsynchronousCalls</c>). Routing failures propagate back through
/// the callback as a <c>DeliveryFailure</c> and the plugin returns a user-actionable
/// error string — never <c>hub.AwaitResponse</c>, which would deadlock the hub.
/// </summary>
public class CollaborationPluginGrainFailureTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    /// <summary>
    /// Foundational contract: posting a <see cref="CreateSuggestedEditRequest"/> to an
    /// address with no registered hub must raise <see cref="DeliveryFailureException"/>
    /// when using <c>AwaitResponse</c> — this test uses the test-only await style that
    /// CLAUDE.md permits in test code. Production plugin code must NOT use
    /// <c>AwaitResponse</c>; it uses Post + RegisterCallback + TCS instead (exercised
    /// by the plugin-level tests below). This test locks the routing contract that
    /// the plugin callback's <c>DeliveryFailure</c> branch depends on.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task SuggestedEditRequest_ToNonExistentGrain_ThrowsDeliveryFailure()
    {
        var nonExistent = new Address("NonExistent", "Document/definitely-not-here");

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Mesh.AwaitResponse(
                new CreateSuggestedEditRequest
                {
                    DocumentId = "NonExistent/Document/definitely-not-here",
                    Position = 0,
                    InsertedText = "test",
                    Author = "test"
                },
                o => o.WithTarget(nonExistent)));

        ex.Should().NotBeOfType<OperationCanceledException>(
            "the routing layer should fail fast, not time out");
        ex.Should().NotBeOfType<TaskCanceledException>();
        ex.GetBaseException().Should().BeOfType<DeliveryFailureException>();
    }

    /// <summary>
    /// Same foundational contract for <see cref="CreateCommentRequest"/>.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentRequest_ToNonExistentGrain_ThrowsDeliveryFailure()
    {
        var nonExistent = new Address("NonExistent", "Document/definitely-not-here");

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Mesh.AwaitResponse(
                new CreateCommentRequest
                {
                    DocumentId = "NonExistent/Document/definitely-not-here",
                    SelectedText = "foo",
                    CommentText = "bar",
                    Author = "test"
                },
                o => o.WithTarget(nonExistent)));

        ex.Should().NotBeOfType<OperationCanceledException>();
        ex.Should().NotBeOfType<TaskCanceledException>();
        ex.GetBaseException().Should().BeOfType<DeliveryFailureException>();
    }

    /// <summary>
    /// Plugin-level coverage for the typical case: the agent passed a path that
    /// doesn't resolve to any existing <see cref="MeshNode"/>. The plugin's early
    /// <c>ops.Get</c> check catches this and returns a user-actionable message
    /// rather than attempting a doomed <c>hub.Post</c>.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task SuggestEdit_NonResolvablePath_ReturnsDocumentNotFound()
    {
        var chat = new NoopChat();
        var plugin = new MeshWeaver.AI.Plugins.CollaborationPlugin(Mesh, chat);

        var result = await plugin.SuggestEdit(
            documentPath: "@/NonExistent/Document/definitely-not-here",
            originalText: "old",
            newText: "new",
            cancellationToken: TestTimeout);

        result.Should().StartWith("Document not found",
            "an unresolvable path must short-circuit before posting to the mesh");
    }

    /// <summary>
    /// Plugin-level coverage for <c>AddComment</c> — same early-exit contract as
    /// <see cref="SuggestEdit_NonResolvablePath_ReturnsDocumentNotFound"/>.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task AddComment_NonResolvablePath_ReturnsDocumentNotFound()
    {
        var chat = new NoopChat();
        var plugin = new MeshWeaver.AI.Plugins.CollaborationPlugin(Mesh, chat);

        var result = await plugin.AddComment(
            documentPath: "@/NonExistent/Document/definitely-not-here",
            selectedText: "foo",
            commentText: "bar",
            cancellationToken: TestTimeout);

        result.Should().StartWith("Document not found");
    }

    /// <summary>
    /// Minimal <see cref="IAgentChat"/> stub — <c>CollaborationPlugin</c> only reads
    /// <c>Context</c> (possibly null) and <c>Context.Path</c> for the author field.
    /// All other members throw.
    /// </summary>
    private sealed class NoopChat : IAgentChat
    {
        public AgentContext? Context => null;

        public void SetContext(AgentContext? applicationContext) => throw new NotImplementedException();
        public void SetSelectedAgent(string? agentName) => throw new NotImplementedException();
        public Task ResumeAsync(AI.Persistence.ChatConversation conversation) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync() => throw new NotImplementedException();
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatMessage> GetResponseAsync(
            IReadOnlyCollection<Microsoft.Extensions.AI.ChatMessage> messages,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<Microsoft.Extensions.AI.ChatMessage> messages,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void SetThreadId(string threadId) => throw new NotImplementedException();
        public void DisplayLayoutArea(MeshWeaver.Layout.LayoutAreaControl layoutAreaControl) => throw new NotImplementedException();
    }
}
