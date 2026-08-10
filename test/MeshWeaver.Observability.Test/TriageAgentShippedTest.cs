using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// The log-watch subsystem names its agent by STRING — <see cref="LogWatchOptions.TriageAgent"/>,
/// default <c>LogTriage</c> — and that string is resolved against the mesh's agent catalog at run
/// time. A name with no agent behind it is not a compile error, not a startup error, and not even
/// a failed request: the incident opens, starts a thread, and the thread quietly answers
/// "Selected agent 'LogTriage' was not found among the available agents". Production error
/// alerting was dead for three days that way.
///
/// <para>This pins the framework half: whatever this subsystem is configured to call must exist in
/// the catalog this binary ships (<see cref="BuiltInAgentProvider"/>, from
/// <c>content/ai/Agent</c>). The DEPLOYED half — the <c>Agent</c> plugin that fills the partition
/// on portals, which is where the agent was actually missing — is pinned by
/// <c>scripts/check-agent-parity.py</c> in MeshWeaver.Plugins, because only that repo's CI can see
/// both copies at once.</para>
/// </summary>
public class TriageAgentShippedTest
{
    [Fact]
    public void TheDefaultTriageAgentIsInTheShippedCatalog()
    {
        var agent = new LogWatchOptions().TriageAgent;

        new BuiltInAgentProvider().GetStaticNodes()
            .Where(node => node.NodeType == "Agent")
            .Select(node => node.Id)
            .Should().Contain(agent,
                $"LogWatchOptions.TriageAgent defaults to '{agent}', and a triage agent that the "
                + "framework does not ship cannot be resolved by any host that has no other source "
                + "for it");
    }
}
