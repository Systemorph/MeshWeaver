#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the fix for the DUPLICATED agent rows in the chat picker (reported 2026-08-25: the
/// ToolsReference agent listed twice, most platform agents three times).
///
/// <para><b>Why it happened.</b> The registry is deliberately LAYERED — the same agent legitimately
/// exists as a platform default (<c>Agent/Tutor</c>), as a package's shipped copy
/// (<c>Essentials/Agent/Tutor</c>) and as the copy plugin install rebases into the viewer's own home
/// (<c>{user}/Agent/Tutor</c>). <see cref="AgentPickerProjection.ProjectAgents"/> de-duplicated by
/// <c>node.Path</c>, which is DISTINCT for every layer, so each layer reached the dropdown as its own
/// row.</para>
///
/// <para><b>Why de-duplicating by path was wrong rather than merely untidy.</b> The identity an agent
/// is ADDRESSED by is its slug (<c>AgentConfiguration.Id</c>) — handoffs, <c>/agent</c>, the chat chip
/// and <c>SetSelectedAgent</c> all use it, and <c>AgentChatClient.CreateAgentsSync</c> keys its dict
/// on exactly that (<c>createdAgents.SetItem(agentConfig.Id, agent)</c>). Three rows sharing one slug
/// therefore already collapsed to ONE executable agent — whichever won a last-write-wins race. The
/// picker offered a choice the execution side could not honour.</para>
///
/// <para>So the projection collapses by slug, and the layer that survives is the MOST SPECIFIC one:
/// the viewer's own copy (the one they can edit) beats a package's, which beats the platform
/// default.</para>
/// </summary>
public class AgentPickerDeduplicationTest
{
    private static readonly JsonSerializerOptions Json = new();

    private const string User = "sglauser";
    private const string Space = "Manufacturing";

    /// <param name="ns">The agent's namespace — the LAYER: <c>Agent</c> (platform),
    /// <c>Essentials/Agent</c> (a package), <c>{user}/Agent</c> (the viewer's own).</param>
    private static MeshNode AgentNode(string slug, string ns, string? name = null) =>
        new(slug, ns)
        {
            NodeType = AgentNodeType.NodeType,
            Name = name ?? slug,
            Content = new AgentConfiguration { Id = slug },
        };

    [Fact]
    public void TheReportedCase_ToolsReferenceIsListedOnce()
    {
        // Exactly what the local mesh held: the package's copy and the viewer's installed copy.
        var snapshot = new[]
        {
            AgentNode("ToolsReference", "Essentials/Agent"),
            AgentNode("ToolsReference", $"{User}/Agent"),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json, userPath: User);

        projected.Should().ContainSingle("one agent slug is one row, however many layers ship it")
            .Which.Path.Should().Be($"{User}/Agent/ToolsReference");
    }

    [Fact]
    public void ThreeLayers_CollapseToTheViewersOwnCopy()
    {
        var snapshot = new[]
        {
            AgentNode("Tutor", AgentPickerProjection.AgentRootNamespace),
            AgentNode("Tutor", "Essentials/Agent"),
            AgentNode("Tutor", $"{User}/Agent"),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json, userPath: User);

        projected.Should().ContainSingle().Which.Path.Should().Be($"{User}/Agent/Tutor",
            "the copy in the viewer's own home is the one they can edit — most specific wins");
    }

    [Fact]
    public void WithoutTheViewersCopy_ThePackageBeatsThePlatformDefault()
    {
        var snapshot = new[]
        {
            AgentNode("Worker", AgentPickerProjection.AgentRootNamespace),
            AgentNode("Worker", "Essentials/Agent"),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json, userPath: User);

        projected.Should().ContainSingle().Which.Path.Should().Be("Essentials/Agent/Worker",
            "a package that ships the agent is more specific than the bare platform default");
    }

    [Fact]
    public void TheContextSpacesCopy_BeatsAnUnrelatedPackage()
    {
        var snapshot = new[]
        {
            AgentNode("Assistant", "Essentials/Agent"),
            AgentNode("Assistant", $"{Space}/Agent"),
        };

        var projected = AgentPickerProjection.ProjectAgents(
            snapshot, Json, userPath: User, spacePath: Space);

        projected.Should().ContainSingle().Which.Path.Should().Be($"{Space}/Agent/Assistant",
            "the space being chatted in is more specific than a package that merely ships the slug");
    }

    [Fact]
    public void DistinctSlugs_AreNeverCollapsed()
    {
        var snapshot = new[]
        {
            AgentNode("Tutor", $"{User}/Agent"),
            AgentNode("Worker", $"{User}/Agent"),
            AgentNode("ToolsReference", "Essentials/Agent"),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json, userPath: User);

        // De-duplication must collapse the LAYERS of one agent, never distinct agents.
        projected.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("ToolsReference", "Tutor", "Worker");
    }

    [Fact]
    public void TheWinnerDoesNotDependOnSnapshotOrder()
    {
        // A synced-query snapshot carries no ordering guarantee; the surviving layer must be
        // decided by specificity, never by which row happened to arrive last.
        var platform = AgentNode("Researcher", AgentPickerProjection.AgentRootNamespace);
        var package = AgentNode("Researcher", "Essentials/Agent");
        var mine = AgentNode("Researcher", $"{User}/Agent");

        foreach (var order in new[]
                 {
                     new[] { platform, package, mine },
                     new[] { mine, package, platform },
                     new[] { package, mine, platform },
                 })
        {
            AgentPickerProjection.ProjectAgents(order, Json, userPath: User)
                .Should().ContainSingle().Which.Path.Should().Be($"{User}/Agent/Researcher");
        }
    }

    [Fact]
    public void SlugMatchingIsCaseInsensitive_LikeTheResolutionSideIs()
    {
        var snapshot = new[]
        {
            AgentNode("toolsreference", "Essentials/Agent"),
            AgentNode("ToolsReference", $"{User}/Agent"),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json, userPath: User);

        projected.Should().ContainSingle(
            "the execution-side dict resolves slugs case-insensitively, so the picker must collapse them too");
    }

    [Fact]
    public void WithNoViewerContext_TheProjectionStillCollapsesDeterministically()
    {
        // The generators (IconGenerator, DescriptionGenerator) project without a user or space.
        var snapshot = new[]
        {
            AgentNode("NodeInitializer", "Essentials/Agent"),
            AgentNode("NodeInitializer", AgentPickerProjection.AgentRootNamespace),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json);

        projected.Should().ContainSingle().Which.Path.Should().Be("Essentials/Agent/NodeInitializer",
            "without a viewer, a package copy is still more specific than the platform default");
    }
}
