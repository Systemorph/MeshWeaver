using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.AI.Completion;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins slash-skill AUTOCOMPLETE — the thing behind typing <c>/</c> in the chat input. The chat routes
/// a <c>/</c> query to <see cref="SkillAutocompleteProvider"/> (ThreadChatView.GetCommandCompletions);
/// this drives that provider the same way and asserts it surfaces the built-in skills from the
/// <c>nodeType:Skill</c> catalog (<c>/agent</c>, <c>/model</c>, <c>/harness</c>, served in-memory here /
/// imported to PG in production). If this is empty, typing <c>/</c> shows nothing in the chat.
/// </summary>
public class SkillAutocompleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI();

    [Fact(Timeout = 60000)]
    public async Task Autocomplete_ListsBuiltInSkills_FromCatalog()
    {
        var provider = new SkillAutocompleteProvider(Mesh.ServiceProvider);

        // Wait until the Skill catalog has surfaced (the agent skill arrives via the nodeType:Skill
        // query, which is async on first load).
        var items = await provider.GetItems("/")
            .Should().Within(15.Seconds())
            .Match(c => c.Any(i => i.Label == "/agent"));

        var labels = items.Select(i => i.Label).ToList();
        labels.Should().Contain("/agent", "the /agent Skill node must surface in autocomplete");
        labels.Should().Contain("/model");
        labels.Should().Contain("/harness");

        // Every skill item inserts a "/word " and is a Command-kind suggestion.
        items.Where(i => i.Label!.StartsWith('/')).Should().AllSatisfy(i =>
        {
            i.InsertText.Should().StartWith("/");
            i.Kind.Should().Be(MeshWeaver.Data.Completion.AutocompleteKind.Command);
        });
    }
}
