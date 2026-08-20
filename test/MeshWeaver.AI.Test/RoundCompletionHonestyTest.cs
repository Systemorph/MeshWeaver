#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 THE INVARIANT: <b>a round may report <see cref="ThreadMessageStatus.Completed"/> only if it
/// actually produced what Completed asserts</b> — every dispatched tool call returned, and the
/// model wrote a closing answer. Anything else must terminate in a state that NAMES what happened.
///
/// <para>Pins both halves of the same defect, end to end through a real round:</para>
/// <list type="bullet">
///   <item><b>#1689</b> — a tool call that was dispatched and never returned. Against
///     <c>origin/main</c> that round persists <c>Status = Completed</c> with the tool entry at the
///     record's default <see cref="ToolCallStatus.Success"/>, so a response narrating work that
///     never happened is indistinguishable from a genuinely successful one.</item>
///   <item><b>#1689 (the side note)</b> — a tool invocation that FAILED, recorded as
///     <c>IsSuccess = true, Status = Success</c>. The harness read only the result STRING and never
///     <see cref="FunctionResultContent.Exception"/>, so a thrown tool was persisted as a success.</item>
///   <item><b>#1715</b> — a silent zero-output closing turn after successful tool calls, terminated
///     as <c>Completed</c> with the abandoned mid-round fragment standing in as the "final answer".</item>
/// </list>
///
/// <para>The scripts are driven by the USER PROMPT rather than mutable fixture state, so the fake
/// chat client stays stateless and the cases cannot leak into each other on a shared mesh.</para>
///
/// <para>Every tool the fake calls is registered <see cref="AIFunction.AsDeclarationOnly"/>:
/// <c>FunctionInvokingChatClient</c> passes a declaration-only call straight through to the caller
/// instead of invoking it, so the fake's script is exactly what the round's streaming loop sees —
/// no synthesized results, no hidden re-invocation, no dependence on how the agent pipeline is
/// assembled.</para>
/// </summary>
public class RoundCompletionHonestyTest(ITestOutputHelper output) : AITestBase(output)
{
    private const string UnfinishedToolCallPrompt = "script:unfinished-tool-call";
    private const string SilentClosingTurnPrompt = "script:silent-closing-turn";
    private const string FailedToolResultPrompt = "script:failed-tool-result";
    private const string HealthyRoundPrompt = "script:healthy-round";

    /// <summary>The failure string from #1689 — a tool that caught its own error and returned prose
    /// that does NOT start with "Error", which is what the harness's heuristic keyed on.</summary>
    private const string ToolFailureText = "CreateEvent failed: were unable to deserialize";

    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new ScriptedChatClientFactory());
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    // ─────────────────────────── #1689 ───────────────────────────

    [Fact]
    public async Task ToolCallNeverReturned_MustNotReportCompleted()
    {
        var (cell, _) = await RunRound(UnfinishedToolCallPrompt);

        cell.Status.Should().Be(ThreadMessageStatus.Error,
            "a round that dispatched a tool call and never got a result did NOT do what Completed "
            + "asserts — reporting Completed is the fabricated success of #1689");
        cell.Text.Should().Contain("Error:",
            "the terminal state must NAME what happened, not just fail silently — same "
            + "'*Error: …*' shape the provider-failure path writes");
        cell.Text.Should().Contain("CreateEvent",
            "the diagnosis must name the tool call that never returned, so it is actionable");

        var entry = cell.ToolCalls.Should().ContainSingle().Subject;
        entry.Status.Should().Be(ToolCallStatus.Failed,
            "a dispatched call with no result must stop carrying the record's default Success");
        entry.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task FailedToolInvocation_MustNotBeRecordedAsSuccess()
    {
        var (cell, _) = await RunRound(FailedToolResultPrompt);

        var entry = cell.ToolCalls.Should().ContainSingle().Subject;
        entry.IsSuccess.Should().BeFalse(
            "FunctionResultContent.Exception is DEFINITIVE evidence the tool failed — the harness "
            + "read only the result string, whose text here deliberately does not start with 'Error'");
        entry.Status.Should().Be(ToolCallStatus.Failed,
            "Status must AGREE with IsSuccess — it was never stamped, so it kept the record's "
            + "default Success and the UI/monitoring read a failed call as a successful one");
        entry.Result.Should().Contain("deserialize", "the failure text must survive onto the entry");

        // …and the boundary: a tool that fails does NOT by itself fail the round. The agent saw the
        // failure and wrote a closing answer, so the round genuinely concluded.
        cell.Status.Should().Be(ThreadMessageStatus.Completed,
            "a recorded tool FAILURE the agent then answered around is an honest completion — the "
            + "guard must not blanket-fail every round that touched a failing tool");
    }

    // ─────────────────────────── #1715 ───────────────────────────

    [Fact]
    public async Task SilentClosingTurnAfterToolCalls_MustNotReportCompleted()
    {
        var (cell, _) = await RunRound(SilentClosingTurnPrompt);

        cell.Status.Should().Be(ThreadMessageStatus.Error,
            "the closing model turn produced zero content, so the round has no final answer — "
            + "Completed asserts one (#1715)");
        cell.Text.Should().Contain("never wrote a closing answer",
            "the user must be told the model never concluded instead of being handed the "
            + "mid-round fragment as if it were the answer");
        cell.Text.Should().Contain("Creating the space",
            "the partial text is preserved — it is evidence, not something to discard");

        cell.ToolCalls.Should().ContainSingle().Which.Status.Should().Be(ToolCallStatus.Success,
            "the tool call itself DID return — only the closing turn is missing");
    }

    [Fact]
    public async Task UnansweredRound_SummaryReadsAsAnError_SoADelegatingParentCannotMisreadIt()
    {
        var (_, thread) = await RunRound(UnfinishedToolCallPrompt);

        // DelegationTool.WaitForDelegationResult hands the child's Summary to the parent
        // VERBATIM, the round resets to Idle either way, and ExtractToolResult classifies a bare
        // string by its "Error" prefix — the convention WaitForDelegationResult itself emits for a
        // cancelled or faulted child. A plain diagnostic sentence here would make the parent
        // record a silent child as a SUCCESSFUL tool result: this bug, one level up.
        thread.Summary.Should().StartWith("Error:",
            "the summary is the delegation seam's failure signal, not just display text");
        thread.Summary.Should().Contain("CreateEvent");
    }

    // ─────────────────────────── the control ───────────────────────────

    [Fact]
    public async Task HealthyRound_WithToolCallAndClosingAnswer_StillCompletes()
    {
        var (cell, thread) = await RunRound(HealthyRoundPrompt);

        cell.Status.Should().Be(ThreadMessageStatus.Completed,
            "a round whose tool returned AND whose model wrote a closing answer is exactly what "
            + "Completed asserts — this is the non-vacuity control for the three guards above");
        cell.Text.Should().Contain("All done");
        cell.ToolCalls.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        thread.Summary.Should().NotBeNullOrEmpty("every terminal write carries a Summary");
    }

    // ─────────────────────────── harness ───────────────────────────

    private async Task<(ThreadMessage Cell, MeshThread Thread)> RunRound(string prompt)
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Round honesty {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();

        var client = GetClient();
        var responseId = await ThreadFlow
            .SubmitAndWait(client, threadPath, prompt, modelName: ScriptedChatClientFactory.ModelName,
                timeout: TimeSpan.FromSeconds(40))
            .Should().Within(TimeSpan.FromSeconds(45)).Emit();

        // Bounded throughout — these are stall bugs, so an unbounded wait would hang exactly the
        // way the defect does.
        var cell = await ThreadFlow
            .ReadMessage(client, threadPath, responseId,
                m => m.Status is ThreadMessageStatus.Completed
                    or ThreadMessageStatus.Error
                    or ThreadMessageStatus.Cancelled,
                TimeSpan.FromSeconds(20))
            .Should().Within(TimeSpan.FromSeconds(25)).Emit();

        var thread = await ThreadFlow
            .ReadThread(client, threadPath, t => !t.IsExecuting, TimeSpan.FromSeconds(20))
            .Should().Within(TimeSpan.FromSeconds(25)).Emit();

        Output.WriteLine($"[{prompt}] cell.Status={cell.Status} toolCalls={cell.ToolCalls.Count} " +
                         $"text='{cell.Text}'");
        return (cell, thread);
    }

    /// <summary>
    /// Stateless scripted chat client: the LAST user message selects the script, so cases cannot
    /// leak into each other and no mutable fixture state is needed.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("ScriptedProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var script = messages
                .Where(m => m.Role == ChatRole.User)
                .Select(m => m.Text ?? string.Empty)
                .LastOrDefault(t => t.StartsWith("script:", StringComparison.Ordinal))
                ?? HealthyRoundPrompt;

            const string callId = "call_honesty_1";
            var call = new FunctionCallContent(callId, "CreateEvent",
                new Dictionary<string, object?> { ["title"] = "Review Carson's posts" });

            switch (script)
            {
                // #1689 — the call is dispatched and the stream ends with NO result for it.
                case UnfinishedToolCallPrompt:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "Adding that as the ninth bullet. ");
                    yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [call] };
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        "Confirmed — the body is now saved correctly with all nine bullets.");
                    break;

                // #1715 — tool call returns, then the CLOSING turn produces nothing at all.
                case SilentClosingTurnPrompt:
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        "Creating the space now — `path` = id = `ClientE");
                    yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [call] };
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Tool,
                        Contents = [new FunctionResultContent(callId, "ClientEpsilon created")]
                    };
                    // …and nothing more: the provider stream ends empty, exactly as observed.
                    break;

                // #1689 side note — the tool FAILED (exception recorded) but its result string does
                // not start with "Error", so the string heuristic alone reads it as a success.
                case FailedToolResultPrompt:
                    yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [call] };
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Tool,
                        Contents =
                        [
                            new FunctionResultContent(callId, ToolFailureText)
                            {
                                Exception = new InvalidOperationException(ToolFailureText)
                            }
                        ]
                    };
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        "I could not create the event; the calendar rejected the payload.");
                    break;

                // The control: tool returns AND the model writes a closing answer.
                default:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "Working on it. ");
                    yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [call] };
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Tool,
                        Contents = [new FunctionResultContent(callId, "event created")]
                    };
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "All done — the event is in your calendar.");
                    break;
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class ScriptedChatClientFactory : IChatClientFactory
    {
        internal const string ModelName = "scripted-round-model";

        public string Name => "ScriptedFactory";
        public IReadOnlyList<string> Models => [ModelName];
        public int Order => 0;

        // Declaration-only: FunctionInvokingChatClient describes it to the model but never invokes
        // it, and passes a call to it straight through to the caller. That is what keeps the fake's
        // script — and only the fake's script — reaching the round's streaming loop.
        private static readonly AITool CreateEventDeclaration =
            AIFunctionFactory
                .Create(([System.ComponentModel.Description("Event title")] string title) => $"created {title}",
                    "CreateEvent", "Creates a calendar event")
                .AsDeclarationOnly();

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new ScriptedChatClient(),
                instructions: config.Instructions ?? "You follow the script in the user message.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [CreateEventDeclaration],
                loggerFactory: null,
                services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }
}
