#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using MeshWeaver.AI.Commands;
using MeshWeaver.AI.Parsing;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for the slash-command pipeline behind the chat input: <see cref="ChatPreParser"/>
/// extraction, <see cref="ChatCommandRegistry"/> registration + lookup, and the command HANDLER
/// contract.
///
/// <para>A command is a HANDLER (<see cref="IChatCommand.Execute"/>) that runs in the thread and
/// TRIGGERS GUI callbacks on the <see cref="CommandContext"/> to inject UI — it never references
/// Blazor. The standard <c>/agent</c> <c>/model</c> <c>/harness</c> pickers ship as
/// <c>nodeType:Command</c> MESH NODES (see <c>CommandHarnessImportSourceTest</c> +
/// <c>BuiltInCommandProvider</c>), not C# classes. A CODE command is only needed for a workflow
/// beyond "pick a node + save it on the composer" — <see cref="HelpCommand"/> and the
/// <see cref="SpaceCommand"/> example below. <see cref="CustomModulePickCommand_Works_WithZeroCoreChanges"/>
/// is the executable copy of <c>Doc/AI/ChatCommands.md</c>.</para>
/// </summary>
public class ChatCommandsTest
{
    private readonly ChatPreParser parser = new();

    /// <summary>Builds a context whose GUI callbacks capture into the given lists (null = headless).</summary>
    private CommandContext Ctx(
        string text,
        List<NodePickerRequest>? pickers = null,
        List<(string Msg, bool IsError)>? statuses = null,
        ChatCommandRegistry? registry = null) => new()
    {
        ParsedCommand = parser.Parse(text).Command
            ?? throw new System.InvalidOperationException($"Expected slash command in '{text}'"),
        CommandRegistry = registry,
        ShowNodePicker = pickers is null ? null : p => pickers.Add(p),
        ShowStatus = statuses is null ? null : (m, e) => statuses.Add((m, e))
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
        registry.Register(new SpaceCommand());
        registry.GetAllCommands().Should().HaveCount(2);
    }

    // ---- The command HANDLER triggers GUI callbacks (no Blazor, no return value) ----

    [Fact]
    public void PickCommand_NoArgs_TriggersNodePicker_WithNullTerm()
    {
        var pickers = new List<NodePickerRequest>();
        new SpaceCommand().Execute(Ctx("/space", pickers));

        var picker = pickers.Should().ContainSingle().Subject;
        picker.Query.Should().Be("nodeType:Space");
        picker.ComposerField.Should().Be("contextPath");
        picker.Title.Should().Be("Choose a Space");
        picker.SearchTerm.Should().BeNull();
    }

    [Fact]
    public void PickCommand_WithName_PassesSearchTerm()
    {
        var pickers = new List<NodePickerRequest>();
        new SpaceCommand().Execute(Ctx("/space Acme", pickers));
        pickers.Should().ContainSingle().Which.SearchTerm.Should().Be("Acme");
    }

    [Fact]
    public void PickCommand_AtPrefix_NormalisedToBareName()
    {
        var pickers = new List<NodePickerRequest>();
        new SpaceCommand().Execute(Ctx("/space @space/Acme", pickers));
        pickers.Should().ContainSingle().Which.SearchTerm.Should().Be("Acme",
            "a '@type/Name' or 'Path/Name' arg normalises to the last segment");
    }

    [Fact]
    public void PickCommand_HeadlessHost_NullCallback_DoesNotThrow()
    {
        // ShowNodePicker is null (no GUI wired) — the handler must null-guard and no-op.
        var ex = Record.Exception(() => new SpaceCommand().Execute(Ctx("/space")));
        ex.Should().BeNull();
    }

    [Fact]
    public void HelpCommand_TriggersStatus_WithCommandList()
    {
        var registry = new ChatCommandRegistry();
        registry.Register(new HelpCommand());
        registry.Register(new SpaceCommand());

        var statuses = new List<(string Msg, bool IsError)>();
        new HelpCommand().Execute(Ctx("/help", statuses: statuses, registry: registry));

        var status = statuses.Should().ContainSingle().Subject;
        status.IsError.Should().BeFalse();
        status.Msg.Should().Contain("/space");
    }

    // ---- Executable docs example: a module's own node-pick command ----

    /// <summary>
    /// A module-defined command: pick a Space and drop it into the composer's context. It needs ONLY
    /// the four declarations — no <see cref="CommandContext"/> field, no chat-view code. It registers
    /// like any other (<c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IChatCommand,
    /// SpaceCommand&gt;())</c>), appears in autocomplete via the registry, and on execution TRIGGERS
    /// the host's generic node picker. This is the executable copy of Doc/AI/ChatCommands.md.
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
    public void CustomModulePickCommand_Works_WithZeroCoreChanges()
    {
        var cmd = new SpaceCommand();

        // It registers + resolves like any built-in command (so it shows up in autocomplete).
        var registry = new ChatCommandRegistry();
        registry.Register(cmd);
        registry.HasCommand("space").Should().BeTrue();

        // And executes through the SAME generic surface — declaring only its query + field + title,
        // triggering the host's node picker via the context callback.
        var pickers = new List<NodePickerRequest>();
        cmd.Execute(Ctx("/space Acme", pickers));
        var picker = pickers.Should().ContainSingle().Subject;
        picker.Query.Should().Be("nodeType:Space");
        picker.ComposerField.Should().Be("contextPath");
        picker.SearchTerm.Should().Be("Acme");
    }
}
