#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Immutable;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the post-refactor selection model: a thread has exactly ONE selection home —
/// <see cref="Thread.Composer"/> (a <see cref="ThreadComposer"/>). The redundant
/// thread-level mirrors (<c>Pending*</c>, <c>SelectedAgentName</c>/<c>SelectedModelName</c>/
/// <c>SelectedHarness</c>, <c>DraftText</c>) were removed — they duplicated the composer
/// and drifted. The chat picker reads + writes the composer's
/// <see cref="ThreadComposer.AgentName"/> / <see cref="ThreadComposer.ModelName"/> /
/// <see cref="ThreadComposer.Harness"/>; the round reads the same fields when it dispatches
/// (see <c>ThreadSubmission.PlanNextRound</c>). These tests pin that single-source shape.
/// </summary>
public class ThreadSelectionPersistenceTest
{
    [Fact]
    public void Thread_DefaultComposer_IsNull_AndHasNoSelection()
    {
        var t = new Thread();
        t.Composer.Should().BeNull("a brand-new thread carries no composer / selection yet");
    }

    [Fact]
    public void Composer_IsTheSingleSelectionHome_ForAgentModelHarness()
    {
        // Picking agent + model + harness writes all three onto the ONE composer.
        var t = new Thread
        {
            Composer = new ThreadComposer
            {
                AgentName = "Agent/Worker",
                ModelName = "_Provider/Anthropic/claude-opus-4-6",
                Harness = "Harness/MeshWeaver"
            }
        };

        t.Composer!.AgentName.Should().Be("Agent/Worker");
        t.Composer.ModelName.Should().Be("_Provider/Anthropic/claude-opus-4-6");
        t.Composer.Harness.Should().Be("Harness/MeshWeaver");
    }

    [Fact]
    public void Composer_PickingOne_PreservesTheOthers()
    {
        // The picker writes a single field; the `with` keeps the other two selections intact.
        var t = new Thread
        {
            Composer = new ThreadComposer
            {
                AgentName = "Agent/Orchestrator",
                ModelName = "_Provider/Anthropic/claude-opus-4-6",
                Harness = "Harness/MeshWeaver"
            }
        };

        // User picks a different agent — only AgentName changes.
        var afterAgentPick = t with { Composer = t.Composer! with { AgentName = "Agent/Worker" } };
        afterAgentPick.Composer!.AgentName.Should().Be("Agent/Worker");
        afterAgentPick.Composer.ModelName.Should().Be("_Provider/Anthropic/claude-opus-4-6");
        afterAgentPick.Composer.Harness.Should().Be("Harness/MeshWeaver");

        // User picks a different model — only ModelName changes.
        var afterModelPick = afterAgentPick with
        {
            Composer = afterAgentPick.Composer! with { ModelName = "_Provider/OpenAI/gpt-4o" }
        };
        afterModelPick.Composer!.AgentName.Should().Be("Agent/Worker");
        afterModelPick.Composer.ModelName.Should().Be("_Provider/OpenAI/gpt-4o");
        afterModelPick.Composer.Harness.Should().Be("Harness/MeshWeaver");

        // User picks a different harness — only Harness changes.
        var afterHarnessPick = afterModelPick with
        {
            Composer = afterModelPick.Composer! with { Harness = "Harness/ClaudeCode" }
        };
        afterHarnessPick.Composer!.AgentName.Should().Be("Agent/Worker");
        afterHarnessPick.Composer.ModelName.Should().Be("_Provider/OpenAI/gpt-4o");
        afterHarnessPick.Composer.Harness.Should().Be("Harness/ClaudeCode");
    }

    [Fact]
    public void Composer_SelectionSurvivesSubmit_DraftAndAttachmentsAreConsumed()
    {
        // Submitting empties the draft + attachments but keeps the sticky agent/model/harness —
        // the next round picks them up. This is the "selection survives, draft is consumed" shape
        // SubmitComposer / StartThread produce.
        var composer = new ThreadComposer
        {
            MessageContent = "hello",
            Attachments = ImmutableList.Create("Some/Node"),
            AgentName = "Agent/Worker",
            ModelName = "_Provider/Anthropic/claude-opus-4-6",
            Harness = "Harness/MeshWeaver"
        };

        var afterSubmit = composer with { MessageContent = null, Attachments = null };

        afterSubmit.MessageContent.Should().BeNull("the draft became the submitted message");
        afterSubmit.Attachments.Should().BeNull("attachments are per-message, consumed on submit");
        afterSubmit.AgentName.Should().Be("Agent/Worker", "agent selection is sticky");
        afterSubmit.ModelName.Should().Be("_Provider/Anthropic/claude-opus-4-6", "model selection is sticky");
        afterSubmit.Harness.Should().Be("Harness/MeshWeaver", "harness selection is sticky");
    }
}
