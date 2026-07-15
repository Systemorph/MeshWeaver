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

    /// <summary>
    /// Model-less environment: the LIVE responder must DEGRADE GRACEFULLY — emit the calm
    /// "configure a language model" notice and NOT submit a thread. This replaced the pre-guard
    /// behavior (submit unconditionally, assert the thread node) when
    /// <c>TrainingSimResponder.IsAnyModelConfigured</c> was introduced: with the credential
    /// resolver registered and no model resolvable, <see cref="TrainingSimResponder.Live"/>
    /// returns the notice BEFORE <c>hub.StartThread</c>. The old assertion (thread lands under
    /// the owner) was therefore impossible and burned its full 60 s wait on every run — one of
    /// the hidden AI.Test failures masked by the CI 6-minute kill. The submit-with-model leg is
    /// e2e-stack territory (needs a real model).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Live_Responder_WithoutModel_EmitsCalmNotice()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(TestUsers.Admin);

        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(new Address(OwnerId)))
            .Should().Within(30.Seconds()).Emit();

        var prompt = $"training-live-{Guid.NewGuid():N}";
        var responder = TrainingSimResponder.Live(client, OwnerId);

        var response = await responder(prompt)
            .Should().Within(30.Seconds()).Emit();

        response!.Code.Should().BeEmpty("the notice keeps the code pane empty");
        var markdown = response.Output.Should().BeOfType<MarkdownControl>()
            .Subject.Markdown!.ToString()!;
        markdown.Should().Contain("configure a language model",
            "a model-less environment must show the calm notice, not a raw factory error");
    }
}

/// <summary>
/// Host-free coverage of the model-config classifier that lets a training cell DEGRADE GRACEFULLY
/// (a calm "configure a model to run this" notice) instead of echoing the raw "ApiKey is missing …"
/// factory error into a course page.
/// </summary>
public class TrainingSimResponderClassificationTest
{
    [Theory]
    [InlineData("ApiKey is missing for model 'glm-5.2'. Configure a ModelProvider node (Provider 'OpenAI').")]
    [InlineData("No AI model is available for this hub.")]
    [InlineData("Selected agent 'Agent/TrainingSim' was found, but creating it failed via factory 'OpenAI' for model 'z-ai/glm-5.2'.")]
    public void LooksLikeMissingModel_TrueForModelConfigSignatures(string text)
        => TrainingSimResponder.LooksLikeMissingModel(text).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Here is a DataGrid of the five orders.")]
    [InlineData("The order total is 1,211.25.")]
    public void LooksLikeMissingModel_FalseForRealAnswersAndEmpty(string? text)
        => TrainingSimResponder.LooksLikeMissingModel(text).Should().BeFalse();
}
