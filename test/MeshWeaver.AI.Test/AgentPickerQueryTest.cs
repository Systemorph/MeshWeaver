using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the AGENT-PICKER query — the <c>[MeshNode(...)]</c> attribute on
/// <see cref="MeshWeaver.AI.ThreadComposer.AgentName"/> and the <c>/agent</c> Command node both use
/// the SAME string. It must list CONVERSATIONAL agents only and EXCLUDE the utility agents
/// (<c>modelTier: utility</c> — ThreadNamer / DescriptionWriter / NodeInitializer / PullRequestWriter),
/// ordered by node <c>Order</c> so the Assistant (Order -1) is first and default-to-first selects it.
///
/// <para>The exclusion lives IN THE QUERY (<c>-content.modelTier:utility</c>) — never replicated as a
/// GUI-side filter (the user's "this must be the query going into the picker" rule). The
/// <c>content.</c> selector resolves identically on the PG backend (<c>n.content-&gt;&gt;'modelTier'</c>)
/// and the in-memory backend (reflection into <c>MeshNode.Content</c>); this test drives the in-memory
/// path. A bare <c>modelTier</c> selector would be inconsistent (PG digs into content, in-memory does
/// not), which is exactly why the query is written with the <c>content.</c> prefix.</para>
/// </summary>
public class AgentPickerQueryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI();

    // The exact query the conversational agent combobox issues — derived from the SINGLE canonical
    // builder (AgentPickerProjection.BuildAgentQuery) + the picker's sort, exactly as
    // ThreadComposerView.BuildAgentPicker composes it. No-context form here exercises the built-in
    // catalog with the utility exclusion + ordering.
    private static readonly string AgentPickerQuery =
        AgentPickerProjection.BuildAgentQuery(excludeUtility: true) + " sort:order";

    [Fact(Timeout = 60000)]
    public async Task AgentPickerQuery_KeepsConversational_ExcludesUtility_AssistantFirst()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Built-in agents surface via the in-memory static provider; wait until the conversational
        // Assistant has surfaced, then assert the shape of the whole result set.
        var items = await mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(AgentPickerQuery))
            .Select(c => c.Items.ToList())
            .Should().Within(15.Seconds())
            .Match(list => list.Any(n => n.Id == "Assistant"));

        var ids = items.Select(n => n.Id).ToList();

        // Conversational agents are listed (Assistant has no modelTier → NULL → kept).
        ids.Should().Contain("Assistant", "the conversational Assistant is the default head of the picker");

        // Utility agents (modelTier: utility) are excluded BY THE QUERY — not by any GUI filter.
        ids.Should().NotContain("ThreadNamer", "ThreadNamer is modelTier:utility");
        ids.Should().NotContain("DescriptionWriter", "DescriptionWriter is modelTier:utility");
        ids.Should().NotContain("NodeInitializer", "NodeInitializer is modelTier:utility");
        ids.Should().NotContain("PullRequestWriter", "PullRequestWriter is modelTier:utility");

        // Ordering lives in the query (sort:order): the Assistant's Order -1 leads, so the picker's
        // default-to-first lands on it.
        items.First().Id.Should().Be("Assistant",
            "sort:order must place the Assistant (Order -1) first so default-to-first selects it");
    }
}
