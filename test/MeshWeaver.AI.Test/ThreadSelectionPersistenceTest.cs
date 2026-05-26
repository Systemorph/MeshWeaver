#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using FluentAssertions;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the sticky agent / model selection that lives on a
/// <see cref="Thread"/> and surfaces through <see cref="ThreadViewModel"/>.
///
/// <para>The chat picker uses two distinct fields:</para>
/// <list type="bullet">
///   <item><b><see cref="Thread.PendingAgentName"/> /
///         <see cref="Thread.PendingModelName"/></b> â€” transient. Stamped
///         when the user submits a message; the server consumes them for
///         that round and clears them. They describe <em>the next
///         execution</em>.</item>
///   <item><b><see cref="Thread.SelectedAgentName"/> /
///         <see cref="Thread.SelectedModelName"/></b> â€” sticky. The user's
///         dropdown choice on this thread; survives reloads. The picker
///         reads these on mount, writes on every change.</item>
/// </list>
///
/// <para>These tests pin those semantics so future refactors don't
/// silently merge them.</para>
/// </summary>
public class ThreadSelectionPersistenceTest
{
    [Fact(Timeout = 30_000)]
    public void Thread_DefaultSelections_AreNull()
    {
        var t = new Thread();

        t.SelectedAgentName.Should().BeNull();
        t.SelectedModelName.Should().BeNull();
        t.PendingAgentName.Should().BeNull();
        t.PendingModelName.Should().BeNull();
    }

    [Fact(Timeout = 30_000)]
    public void Thread_PendingAndSelected_AreIndependent()
    {
        // The picker writes to Selected; the submission stamps Pending.
        // Setting one does not touch the other.
        var t = new Thread
        {
            SelectedAgentName = "Worker",
            SelectedModelName = "claude-sonnet-4-6"
        };

        t.PendingAgentName.Should().BeNull();
        t.PendingModelName.Should().BeNull();
        t.SelectedAgentName.Should().Be("Worker");
        t.SelectedModelName.Should().Be("claude-sonnet-4-6");
    }

    [Fact(Timeout = 30_000)]
    public void Thread_WithExpression_PreservesUnchangedFields()
    {
        // Picker change (Selected) must not zero the queued submission's
        // Pending fields, nor vice versa.
        var t = new Thread
        {
            PendingAgentName = "Orchestrator",
            PendingModelName = "claude-opus-4-6",
            SelectedAgentName = "Orchestrator",
            SelectedModelName = "claude-opus-4-6"
        };

        // User picks a new agent â€” only Selected changes.
        var afterPick = t with { SelectedAgentName = "Worker" };

        afterPick.SelectedAgentName.Should().Be("Worker");
        afterPick.SelectedModelName.Should().Be("claude-opus-4-6");
        afterPick.PendingAgentName.Should().Be("Orchestrator");
        afterPick.PendingModelName.Should().Be("claude-opus-4-6");
    }

    [Fact(Timeout = 30_000)]
    public void ThreadViewModel_CarriesSelectionFields()
    {
        var vm = new ThreadViewModel
        {
            ThreadPath = "rbuergi/_Thread/hello-1066",
            SelectedAgentName = "Worker",
            SelectedModelName = "claude-sonnet-4-6"
        };

        vm.SelectedAgentName.Should().Be("Worker");
        vm.SelectedModelName.Should().Be("claude-sonnet-4-6");
    }

    [Fact(Timeout = 30_000)]
    public void ThreadViewModel_Equals_IncludesSelectionFields()
    {
        // Custom Equals override has to track these so the chat view's
        // re-render gate fires when the picker changes.
        var a = new ThreadViewModel { SelectedAgentName = "Worker" };
        var b = new ThreadViewModel { SelectedAgentName = "Coder" };
        var c = new ThreadViewModel { SelectedAgentName = "Worker" };

        a.Equals(b).Should().BeFalse(
            "different SelectedAgentName must produce inequality");
        a.Equals(c).Should().BeTrue(
            "same SelectedAgentName + everything else default = equal");
        a.GetHashCode().Should().Be(c.GetHashCode());
    }

    [Fact(Timeout = 30_000)]
    public void ThreadViewModel_Equals_IncludesSelectedModel()
    {
        var a = new ThreadViewModel { SelectedModelName = "claude-sonnet-4-6" };
        var b = new ThreadViewModel { SelectedModelName = "claude-opus-4-6" };
        var c = new ThreadViewModel { SelectedModelName = "claude-sonnet-4-6" };

        a.Equals(b).Should().BeFalse();
        a.Equals(c).Should().BeTrue();
        a.GetHashCode().Should().Be(c.GetHashCode());
    }
}
