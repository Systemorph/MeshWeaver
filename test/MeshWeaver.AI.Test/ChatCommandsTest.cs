#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Threading.Tasks;
using MeshWeaver.AI.Commands;
using MeshWeaver.AI.Parsing;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for the slash-command pipeline behind the chat input: <see cref="ChatPreParser"/>
/// extraction, <see cref="ChatCommandRegistry"/> registration + lookup, and the production
/// node-pick commands (<c>/agent</c>, <c>/model</c>, <c>/harness</c>).
///
/// <para>The command surface is GENERIC: every node-pick command is a tiny
/// <see cref="MeshNodePickCommand"/> subclass that declares only its mesh query + target composer
/// field + title, and returns a <see cref="NodePickerRequest"/>. The host renders one generic node
/// picker. <see cref="CustomModulePickCommand_Works_WithZeroCoreChanges"/> proves a MODULE can add
/// its own such command with no changes to <see cref="CommandContext"/> or the chat view — the
/// executable counterpart of <c>Doc/AI/ChatCommands.md</c>.</para>
/// </summary>
public class ChatCommandsTest
{
    private readonly ChatPreParser parser = new();

    private CommandContext Ctx(string text) => new()
    {
        ParsedCommand = parser.Parse(text).Command
            ?? throw new System.InvalidOperationException($"Expected slash command in '{text}'")
    };

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
        result.Command!.Name.Should().Be("model");
        result.Command.RawArguments.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void Parse_SlashHelp_NoArguments()
    {
        var result = parser.Parse("/help");
        result.Command!.Name.Should().Be("help");
        result.Command.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SlashNotAtStart_NotTreatedAsCommand()
    {
        parser.Parse("Use /agent to switch.").Command.Should().BeNull();
    }

    // ---- ChatCommandRegistry ----

    [Fact]
    public void Registry_Register_ResolvesByNameAndAliases()
    {
        var registry = new ChatCommandRegistry();
        registry.Register(new HelpCommand());
        registry.HasCommand("help").Should().BeTrue();
        registry.HasCommand("?").Should().BeTrue();
        registry.HasCommand("HELP").Should().BeTrue();
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

    // ---- Node-pick commands: each declares query + composer field + title ----

    [Fact]
    public async Task AgentCommand_NoArgs_RequestsAgentPicker()
    {
        var result = await new AgentCommand().ExecuteAsync(Ctx("/agent"), TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue();
        result.Picker.Should().NotBeNull();
        result.Picker!.Query.Should().Be("namespace:Agent nodeType:Agent");
        result.Picker.ComposerField.Should().Be("agentName");
        result.Picker.Title.Should().Be("Choose an agent");
        result.Picker.SearchTerm.Should().BeNull();
    }

    [Fact]
    public async Task AgentCommand_WithName_PassesSearchTerm()
    {
        var result = await new AgentCommand().ExecuteAsync(Ctx("/agent Worker"), TestContext.Current.CancellationToken);
        result.Picker!.SearchTerm.Should().Be("Worker");
        result.Picker.ComposerField.Should().Be("agentName");
    }

    [Fact]
    public async Task AgentCommand_AtPrefix_NormalisedToBareName()
    {
        var result = await new AgentCommand().ExecuteAsync(Ctx("/agent @agent/Worker"), TestContext.Current.CancellationToken);
        result.Picker!.SearchTerm.Should().Be("Worker", "a '@type/Name' or 'Path/Name' arg normalises to the last segment");
    }

    [Fact]
    public async Task ModelCommand_NoArgs_RequestsModelPicker_OnTheConfiguredCatalogQuery()
    {
        var result = await new ModelCommand().ExecuteAsync(Ctx("/model"), TestContext.Current.CancellationToken);
        result.Picker!.Query.Should().Be("namespace:_Provider nodeType:LanguageModel scope:descendants");
        result.Picker.ComposerField.Should().Be("modelName");
        result.Picker.Title.Should().Be("Choose a model");
    }

    [Fact]
    public async Task HarnessCommand_NoArgs_RequestsHarnessPicker()
    {
        var result = await new HarnessCommand().ExecuteAsync(Ctx("/harness"), TestContext.Current.CancellationToken);
        result.Picker!.Query.Should().Be("namespace:Harness nodeType:Harness");
        result.Picker.ComposerField.Should().Be("harness");
        result.Picker.Title.Should().Be("Choose a harness");
    }

    // ---- Executable docs example: a module's own node-pick command ----

    /// <summary>
    /// A module-defined command: pick a Space and drop it into the composer's context. It needs
    /// ONLY the four declarations — no <see cref="CommandContext"/> field, no chat-view code. It
    /// registers like any other (<c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IChatCommand,
    /// SpaceCommand&gt;())</c>), appears in autocomplete via the registry, and the host renders the
    /// generic picker for its query. This is the executable copy of Doc/AI/ChatCommands.md.
    /// </summary>
    private sealed class SpaceCommand : MeshNodePickCommand
    {
        public override string Name => "space";
        public override string Description => "Pick a Space";
        protected override string Query => "nodeType:Space";
        protected override string ComposerField => "contextPath";
        protected override string Title => "Choose a Space";
    }

    [Fact]
    public async Task CustomModulePickCommand_Works_WithZeroCoreChanges()
    {
        var cmd = new SpaceCommand();

        // It registers + resolves like any built-in command (so it shows up in autocomplete).
        var registry = new ChatCommandRegistry();
        registry.Register(cmd);
        registry.HasCommand("space").Should().BeTrue();

        // And executes through the SAME generic surface — declaring only its query + field + title.
        var result = await cmd.ExecuteAsync(Ctx("/space Acme"), TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue();
        result.Picker!.Query.Should().Be("nodeType:Space");
        result.Picker.ComposerField.Should().Be("contextPath");
        result.Picker.SearchTerm.Should().Be("Acme");
    }
}
