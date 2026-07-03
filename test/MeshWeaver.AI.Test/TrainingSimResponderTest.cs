#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Coverage of the PromptCell LIVE adapter (<see cref="TrainingSimResponder"/>):
/// <list type="number">
///   <item><see cref="TrainingSimResponder.Project"/> splits an assistant reply
///   into the cell's three-pane shape — first fenced code block → code pane,
///   remainder → output markdown.</item>
///   <item>Subscribing the LIVE responder submits the prompt through the
///   canonical <c>hub.StartThread</c> surface: a thread node is created under
///   the exercise namespace with <c>Composer.AgentName = TrainingSim</c> and
///   the prompt queued as its first message. Model-less environment — the
///   REPLY leg (a real agent round) is e2e-stack territory; this pins the
///   submission side effect the adapter owes the cell.</item>
/// </list>
/// </summary>
public class TrainingSimResponderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OwnerId = "Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public void Project_Splits_First_Fence_Into_Code_And_Output()
    {
        var reply = new ThreadMessage
        {
            Role = "assistant",
            Text = """
                Here's the cell:

                ```csharp
                var totals = sales.GroupBy(s => s.Region);
                Controls.DataGrid(totals)
                ```

                The grid shows one row per region.
                """
        };

        var projected = TrainingSimResponder.Project(reply);

        projected.Code.Should().Contain("GroupBy(s => s.Region)");
        projected.Code.Should().NotContain("```", "the fence lines are stripped — the cell re-fences itself");
        var outputMarkdown = projected.Output.Should().BeOfType<MarkdownControl>()
            .Subject.Markdown!.ToString()!;
        outputMarkdown.Should().Contain("one row per region");
        outputMarkdown.Should().NotContain("GroupBy", "the code lives in the code pane, not the output");
    }

    [Fact]
    public void Project_Without_Fence_Yields_Empty_Code_And_Full_Output()
    {
        var projected = TrainingSimResponder.Project(new ThreadMessage
        {
            Role = "assistant",
            Text = "Just an explanation, no code."
        });
        projected.Code.Should().BeEmpty();
        projected.Output.Should().BeOfType<MarkdownControl>()
            .Which.Markdown!.ToString().Should().Contain("Just an explanation");
    }

    [Fact(Timeout = 90_000)]
    public async Task Live_Responder_Submits_Thread_With_TrainingSim_Agent()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(TestUsers.Admin);

        var client = GetClient();
        // Owner partition live (onboarded) — same precondition ThreadCreatableFromHomeTest pins.
        await client.Observe(new PingRequest(), o => o.WithTarget(new Address(OwnerId)))
            .Should().Within(30.Seconds()).Emit();

        var prompt = $"training-live-{Guid.NewGuid():N}";
        var responder = TrainingSimResponder.Live(client, OwnerId);

        // Subscribe = submit. No model is configured here, so the REPLY may
        // never complete — the assertion below is on the canonical submission
        // side effect, not the round.
        using var subscription = responder(prompt).Subscribe(_ => { }, _ => { });

        // The thread node lands under {owner}/_Thread with the TrainingSim
        // agent selected on its composer and our prompt as the queued/first
        // user message.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var thread = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{OwnerId}/_Thread scope:children nodeType:{ThreadNodeType.NodeType}"))
            .SelectMany(change => change.Items)
            .Select(node => node.ContentAs<MeshThread>(Mesh.JsonSerializerOptions))
            .Should().Within(60.Seconds()).Match(t => t is not null
                && t.Composer != null
                && t.Composer.AgentName == TrainingSimResponder.AgentName
                && (t.PendingUserMessages.Values.Any(m => m.Text == prompt)
                    || t.UserMessageIds.Count > 0));

        thread!.Composer!.AgentName.Should().Be(TrainingSimResponder.AgentName,
            "the LIVE adapter selects the dedicated training agent on the composer");
    }
}
