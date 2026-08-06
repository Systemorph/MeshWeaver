#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the merge a package install performs on a user's <see cref="AiSettings"/> sources.
///
/// <para><b>The defect this closes.</b> A package installs its agents into its own partition
/// (<c>Essentials/Agent</c>), but the agent picker resolves from each user's persisted
/// <c>AgentQueries</c> — written once and never revisited. A user whose settings predate the install
/// asked for <c>namespace:{user}/Agent|{space}/Agent|Agent</c> forever, none of which match, and got
/// an EMPTY PICKER with nothing in any log. On the live portal that was every agent: all 15 lived
/// under <c>Essentials/Agent</c> and <c>Feedback/Agent</c>, and <c>namespace:Agent</c> returned zero.</para>
/// </summary>
public class PackageSourceRegistrationTest
{
    private const string Proj = AgentPickerProjection.RegistryProjection;

    [Fact]
    public void MergeAgentSource_AddsThePackagesAgentNamespace()
    {
        var merged = AiSettingsNodeType.MergeAgentSource(new AiSettings(), "Essentials");

        merged.AgentQueries.Should().Contain("namespace:Essentials/Agent nodeType:Agent" + Proj);
    }

    [Fact]
    public void MergeAgentSource_SeedsTheCodeDefaultsFirst_SoInstallingNeverDropsTheStandardSources()
    {
        // An empty list means "code defaults". Appending to it must not silently replace them —
        // that would trade one missing source for three.
        var merged = AiSettingsNodeType.MergeAgentSource(new AiSettings(), "Essentials");

        merged.AgentQueries.Take(AiSettingsNodeType.DefaultAgentQueryTemplates.Length)
            .Should().Equal(AiSettingsNodeType.DefaultAgentQueryTemplates);
    }

    [Fact]
    public void MergeAgentSource_IsIdempotent()
    {
        // It runs on install, on every package update, and on every startup repair — so the
        // already-correct case must be a genuine no-op, not a growing list.
        var once = AiSettingsNodeType.MergeAgentSource(new AiSettings(), "Essentials");
        var twice = AiSettingsNodeType.MergeAgentSource(once, "Essentials");

        twice.AgentQueries.Should().Equal(once.AgentQueries);
    }

    [Fact]
    public void MergeAgentSource_PreservesAUsersOwnCustomRows()
    {
        var custom = new AiSettings
        {
            AgentQueries = ImmutableArray.Create("namespace:Mine/Agent nodeType:Agent"),
        };

        var merged = AiSettingsNodeType.MergeAgentSource(custom, "Essentials");

        merged.AgentQueries.Should().Equal(
            "namespace:Mine/Agent nodeType:Agent",
            "namespace:Essentials/Agent nodeType:Agent" + Proj);
    }

    [Fact]
    public void MergePackageSources_RegistersAgentsAndSkillsTogether()
    {
        // One call, both registries — a caller cannot register agents and forget skills, which is
        // exactly how skills ended up with the same latent hole (AddSkillSource had no callers).
        var merged = AiSettingsNodeType.MergePackageSources(new AiSettings(), "Essentials");

        merged.AgentQueries.Should().Contain("namespace:Essentials/Agent nodeType:Agent" + Proj);
        merged.SkillQueries.Should().Contain("namespace:Essentials/Skill nodeType:Skill");
    }

    [Fact]
    public void MergePackageSources_IsIdempotentAcrossBothRegistries()
    {
        var once = AiSettingsNodeType.MergePackageSources(new AiSettings(), "Essentials");
        var twice = AiSettingsNodeType.MergePackageSources(once, "Essentials");

        twice.AgentQueries.Should().Equal(once.AgentQueries);
        twice.SkillQueries.Should().Equal(once.SkillQueries);
    }

    [Fact]
    public void MergePackageSources_AccumulatesAcrossPackages()
    {
        // The live portal ships several free packages; installing one must not evict another.
        var merged = AiSettingsNodeType.MergePackageSources(
            AiSettingsNodeType.MergePackageSources(new AiSettings(), "Essentials"), "Feedback");

        merged.AgentQueries.Should().Contain("namespace:Essentials/Agent nodeType:Agent" + Proj);
        merged.AgentQueries.Should().Contain("namespace:Feedback/Agent nodeType:Agent" + Proj);
    }

    [Fact]
    public void TheDefaultAgentTemplates_AreTheCanonicalBuilderOutput()
    {
        // One definition: the settings defaults and the picker's builder must not drift, or a user
        // with default settings resolves a different registry than the code says they should.
        AiSettingsNodeType.DefaultAgentQueryTemplates.Should().Equal(
            AgentPickerProjection.BuildAgentQueries("{userPath}", "{currentPath}"));
    }
}
