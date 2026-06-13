#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI.Commands;
using MeshWeaver.AI.Parsing;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests covering the slash-command pipeline that powers the chat
/// breadcrumb (in <c>ThreadChatView.razor</c>): <see cref="ChatPreParser"/>
/// extraction, <see cref="ChatCommandRegistry"/> registration + lookup, and
/// the two production commands users will actually type â€” <c>/agent</c>
/// and <c>/model</c>. The dropdowns were removed in the same change set; if
/// these tests pass, switching agents/models from the input box still works.
/// </summary>
public class ChatCommandsTest
{
    private readonly ChatPreParser parser = new();

    // ---- ChatPreParser ----

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmptyResult()
    {
        var result = parser.Parse(null);
        result.Command.Should().BeNull();
        result.AgentReference.Should().BeNull();
        result.ModelReference.Should().BeNull();

        parser.Parse("   ").Command.Should().BeNull();
    }

    [Fact]
    public void Parse_PlainText_NoCommandNoReferences()
    {
        var result = parser.Parse("Hello, please summarise this.");
        result.Command.Should().BeNull();
        result.ProcessedText.Should().Be("Hello, please summarise this.");
        result.ShouldSendToAI.Should().BeTrue();
    }

    [Fact]
    public void Parse_SlashAgentWithName_ReturnsCommand()
    {
        var result = parser.Parse("/agent Worker");
        result.Command.Should().NotBeNull();
        result.Command!.Name.Should().Be("agent");
        result.Command.Arguments.Should().Equal("Worker");
        result.Command.RawArguments.Should().Be("Worker");
        result.ShouldSendToAI.Should().BeFalse();
        result.ProcessedText.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SlashModelWithDottedName_PreservesArg()
    {
        var result = parser.Parse("/model gpt-4o-mini");
        result.Command.Should().NotBeNull();
        result.Command!.Name.Should().Be("model");
        result.Command.RawArguments.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void Parse_SlashHelp_NoArguments()
    {
        var result = parser.Parse("/help");
        result.Command.Should().NotBeNull();
        result.Command!.Name.Should().Be("help");
        result.Command.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AgentReferenceInMessage_ExtractsButNoCommand()
    {
        var result = parser.Parse("@agent/Worker can you help?");
        result.Command.Should().BeNull();
        result.AgentReference.Should().Be("Worker");
    }

    [Fact]
    public void Parse_SlashNotAtStart_NotTreatedAsCommand()
    {
        // "/" must be at the start; mid-message is plain text.
        var result = parser.Parse("Use /agent to switch.");
        result.Command.Should().BeNull();
    }

    // ---- ChatCommandRegistry ----

    [Fact]
    public void Registry_Register_ResolvesByNameAndAliases()
    {
        var registry = new ChatCommandRegistry();
        registry.Register(new HelpCommand());

        registry.HasCommand("help").Should().BeTrue();
        registry.HasCommand("?").Should().BeTrue(); // alias
        registry.HasCommand("HELP").Should().BeTrue(); // case-insensitive

        registry.TryGetCommand("help", out var byName).Should().BeTrue();
        registry.TryGetCommand("?", out var byAlias).Should().BeTrue();
        byName.Should().BeSameAs(byAlias);
    }

    [Fact]
    public void Registry_GetAllCommands_DedupesAliases()
    {
        var registry = new ChatCommandRegistry();
        registry.Register(new HelpCommand());
        registry.Register(new AgentCommand());

        registry.GetAllCommands().Should().HaveCount(2);
    }

    [Fact]
    public void Registry_UnknownCommand_TryGetReturnsFalse()
    {
        var registry = new ChatCommandRegistry();
        registry.TryGetCommand("nope", out var cmd).Should().BeFalse();
        cmd.Should().BeNull();
    }

    // ---- AgentCommand ----

    [Fact]
    public async Task AgentCommand_NoArgs_RequestsPickerWidget()
    {
        var (cmd, ctx, switched) = MakeAgentCommandContext(parsed: ParseSlash("/agent"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        // No-args = "show me the picker" â€” Success + Widget set, agent list
        // still listed in the message body as a fallback for non-Blazor hosts.
        result.Success.Should().BeTrue();
        result.Widget.Should().Be(ChatWidget.AgentPicker);
        result.Message.Should().Contain("Worker");
        result.Message.Should().Contain("Coder");
        switched.Value.Should().BeNull();
    }

    [Fact]
    public async Task AgentCommand_KnownAgent_SwitchesAndReturnsOk()
    {
        var (cmd, ctx, switched) = MakeAgentCommandContext(parsed: ParseSlash("/agent Worker"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Worker");
        switched.Value.Should().NotBeNull();
        switched.Value!.Name.Should().Be("Worker");
    }

    [Fact]
    public async Task AgentCommand_AtPrefix_AlsoMatches()
    {
        var (cmd, ctx, switched) = MakeAgentCommandContext(parsed: ParseSlash("/agent @agent/Worker"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        switched.Value!.Name.Should().Be("Worker");
    }

    [Fact]
    public async Task AgentCommand_CaseInsensitiveMatch()
    {
        var (cmd, ctx, switched) = MakeAgentCommandContext(parsed: ParseSlash("/agent worker"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        switched.Value!.Name.Should().Be("Worker"); // returns canonical-case from dict
    }

    [Fact]
    public async Task AgentCommand_UnknownAgent_ReturnsError_DoesNotSwitch()
    {
        var (cmd, ctx, switched) = MakeAgentCommandContext(parsed: ParseSlash("/agent Ghost"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Ghost");
        switched.Value.Should().BeNull();
    }

    // ---- ModelCommand ----

    [Fact]
    public async Task ModelCommand_NoArgs_RequestsPickerWidget()
    {
        var (cmd, ctx, switched) = MakeModelCommandContext(parsed: ParseSlash("/model"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.Widget.Should().Be(ChatWidget.ModelPicker);
        result.Message.Should().Contain("gpt-4o-mini");
        result.Message.Should().Contain("claude-sonnet");
        switched.Value.Should().BeNull();
    }

    [Fact]
    public async Task ModelCommand_KnownModel_SwitchesAndReturnsOk()
    {
        var (cmd, ctx, switched) = MakeModelCommandContext(parsed: ParseSlash("/model gpt-4o-mini"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        switched.Value!.Name.Should().Be("gpt-4o-mini");
        // The confirmation is surfaced in the chat output (lastCommandStatus) so the user
        // sees that the model changed — must name the model that was switched to.
        result.Message.Should().Contain("gpt-4o-mini");
    }

    [Fact]
    public async Task ModelCommand_UnknownModel_ReturnsError_DoesNotSwitch()
    {
        var (cmd, ctx, switched) = MakeModelCommandContext(parsed: ParseSlash("/model nope"));

        var result = await cmd.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        switched.Value.Should().BeNull();
    }

    // ---- helpers ----

    private ParsedCommand ParseSlash(string text) =>
        parser.Parse(text).Command ?? throw new System.InvalidOperationException(
            $"Expected slash command in '{text}'");

    private static (AgentCommand cmd, CommandContext ctx, Box<AgentDisplayInfo?> switched)
        MakeAgentCommandContext(ParsedCommand parsed)
    {
        AgentDisplayInfo MakeAgent(string name) => new()
        {
            Name = name,
            Path = $"Agent/{name}",
            Description = $"Stub agent {name}",
            AgentConfiguration = new AgentConfiguration { Id = name }
        };
        var agents = new Dictionary<string, AgentDisplayInfo>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Worker"] = MakeAgent("Worker"),
            ["Coder"] = MakeAgent("Coder"),
        };
        var switched = new Box<AgentDisplayInfo?>();
        var ctx = new CommandContext
        {
            ParsedCommand = parsed,
            AvailableAgents = agents,
            SetCurrentAgent = a => switched.Value = a
        };
        return (new AgentCommand(), ctx, switched);
    }

    private static (ModelCommand cmd, CommandContext ctx, Box<ModelInfo?> switched)
        MakeModelCommandContext(ParsedCommand parsed)
    {
        var models = new List<ModelInfo>
        {
            new() { Name = "gpt-4o-mini", Provider = "OpenAI" },
            new() { Name = "claude-sonnet", Provider = "Anthropic" },
        };
        var switchedModel = new Box<ModelInfo?>();
        var ctx = new CommandContext
        {
            ParsedCommand = parsed,
            AvailableAgents = new Dictionary<string, AgentDisplayInfo>(),
            SetCurrentAgent = _ => { },
            AvailableModels = models,
            SetCurrentModel = m => switchedModel.Value = m
        };
        return (new ModelCommand(), ctx, switchedModel);
    }

    /// <summary>Out-parameter substitute for closures used in lambdas.</summary>
    private sealed class Box<T> { public T? Value { get; set; } }
}
