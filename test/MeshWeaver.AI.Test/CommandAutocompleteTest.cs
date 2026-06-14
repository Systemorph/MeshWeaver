using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.AI.Completion;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins slash-command AUTOCOMPLETE — the thing behind typing <c>/</c> in the chat input. The chat
/// routes a <c>/</c> query to <see cref="CommandAutocompleteProvider"/> (ThreadChatView.GetCommandCompletions);
/// this drives that provider the same way and asserts it surfaces the built-in commands from BOTH
/// sources it blends: the <c>nodeType:Command</c> catalog (<c>/agent</c>, <c>/model</c>, <c>/harness</c>,
/// served in-memory here / imported to PG in production) AND the C# <see cref="MeshWeaver.AI.Commands.IChatCommand"/>
/// registry (<c>/help</c>). If this is empty, typing <c>/</c> shows nothing in the chat.
/// </summary>
public class CommandAutocompleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI();

    [Fact(Timeout = 60000)]
    public void Autocomplete_ListsBuiltInCommands_FromCatalogAndRegistry()
    {
        var provider = new CommandAutocompleteProvider(Mesh.ServiceProvider);

        // Wait until the Command catalog has surfaced (the agent command arrives via the nodeType:Command
        // query, which is async on first load); the registry command (/help) is synchronous.
        var items = provider.GetItems("/")
            .Should().Within(15.Seconds())
            .Match(c => c.Any(i => i.Label == "/agent"));

        var labels = items.Select(i => i.Label).ToList();
        labels.Should().Contain("/agent", "the /agent Command node must surface in autocomplete");
        labels.Should().Contain("/model");
        labels.Should().Contain("/harness");
        labels.Should().Contain("/help", "the C# /help command (registry) blends in alongside the catalog");

        // Every command item inserts a "/word " and is a Command-kind suggestion.
        items.Where(i => i.Label!.StartsWith('/')).Should().AllSatisfy(i =>
        {
            i.InsertText.Should().StartWith("/");
            i.Kind.Should().Be(MeshWeaver.Data.Completion.AutocompleteKind.Command);
        });
    }
}
