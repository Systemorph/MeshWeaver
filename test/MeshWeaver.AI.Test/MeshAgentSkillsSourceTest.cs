#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI.Stores;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Integration coverage for <see cref="MeshAgentSkillsSource"/> — our <c>nodeType:Skill</c> nodes
/// served through the Microsoft Agent Framework's <c>AgentSkillsSource</c> abstraction. Real mesh,
/// nothing mocked.
///
/// <para>What matters here is that the platform's skill layering survives the adaptation: a space's
/// skill is found anywhere in its partition subtree, and a more specific layer overrides a less
/// specific one of the same name.</para>
/// </summary>
public class MeshAgentSkillsSourceTest(ITestOutputHelper output) : AITestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private Task<MeshNode> SeedSkill(string ns, string id, string description, string instructions)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.CreateNode(new MeshNode(id, ns)
        {
            Name = id,
            Description = description,
            NodeType = SkillNodeType.NodeType,
            Content = new SkillDefinition { Instructions = instructions },
        }).FirstAsync().ToTask();
    }

    private static Task<IReadOnlyCollection<AgentSkill>> Await(
        MeshAgentSkillsSource source, Func<IReadOnlyCollection<AgentSkill>, bool> until) =>
        source.GetSkills().Where(until).FirstAsync().Timeout(Bound).ToTask();

    [Fact]
    public async Task ASpacesSkill_IsFoundAnywhereInItsPartitionSubtree()
    {
        // Deliberately NOT in a flat "{partition}/Skill" namespace — this is the case a flat
        // exact-membership query misses, and the reason layers 2 and 3 scope to the subtree.
        var space = $"space{Guid.NewGuid():N}";
        await SeedSkill($"{space}/Reports/Skill", "quarterly-close",
            "How to run the quarterly close.", "Step 1. Reconcile.");

        var source = new MeshAgentSkillsSource(Mesh, contextPath: $"{space}/Reports/Q3");

        var skills = await Await(source, list => list.Any(s => s.Frontmatter.Name == "quarterly-close"));

        var skill = skills.Single(s => s.Frontmatter.Name == "quarterly-close");
        skill.Frontmatter.Description.Should().Be("How to run the quarterly close.");
        (await skill.GetContentAsync()).Should().Be("Step 1. Reconcile.");
    }

    [Fact]
    public async Task TheSkillsMeshPath_TravelsOnTheFrontmatterMetadata()
    {
        var space = $"space{Guid.NewGuid():N}";
        await SeedSkill($"{space}/Skill", "deploy", "How to deploy.", "Run the pipeline.");

        var source = new MeshAgentSkillsSource(Mesh, contextPath: space);
        var skills = await Await(source, list => list.Any(s => s.Frontmatter.Name == "deploy"));

        skills.Single(s => s.Frontmatter.Name == "deploy")
            .Frontmatter.Metadata!["meshPath"].Should().Be($"{space}/Skill/deploy",
                "the mesh path is the skill's identity everywhere else in the platform");
    }

    [Fact]
    public async Task AUsersSkill_OverridesASpaceSkillOfTheSameName()
    {
        var space = $"space{Guid.NewGuid():N}";
        var user = $"user{Guid.NewGuid():N}";
        await SeedSkill($"{space}/Skill", "review", "Space version.", "space instructions");
        await SeedSkill($"{user}/Skill", "review", "User version.", "user instructions");

        var source = new MeshAgentSkillsSource(Mesh, contextPath: space, userPath: user);

        var skills = await Await(source, list => list.Any(s => s.Frontmatter.Name == "review"));

        skills.Where(s => s.Frontmatter.Name == "review").Should().ContainSingle(
            "a name defined in two layers resolves to exactly one skill, not a duplicate pair");
        var winner = skills.Single(s => s.Frontmatter.Name == "review");
        winner.Frontmatter.Description.Should().Be("User version.",
            "precedence is most-specific-first: user beats space");
        (await winner.GetContentAsync()).Should().Be("user instructions");
    }

    [Fact]
    public async Task ABehaviourOnlySkill_IsAdvertisedWithAnEmptyBody()
    {
        var space = $"space{Guid.NewGuid():N}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode("pick-model", $"{space}/Skill")
        {
            Name = "pick-model",
            Description = "Opens the model picker.",
            NodeType = SkillNodeType.NodeType,
            // No Instructions: a pure behaviour skill.
            Content = new SkillDefinition
            {
                Action = new SkillAction { Kind = SkillActionKind.Pick, Query = "nodeType:LanguageModel" },
            },
        }).FirstAsync().ToTask();

        var source = new MeshAgentSkillsSource(Mesh, contextPath: space);
        var skills = await Await(source, list => list.Any(s => s.Frontmatter.Name == "pick-model"));

        (await skills.Single(s => s.Frontmatter.Name == "pick-model").GetContentAsync())
            .Should().BeEmpty("a behaviour skill has nothing to inject — that is a no-op, not an error");
    }

    [Fact]
    public async Task ANodeIdThatIsNotAValidAgentFrameworkName_IsFoldedIntoOne()
    {
        var space = $"space{Guid.NewGuid():N}";
        await SeedSkill($"{space}/Skill", "Provider Keys", "Manage provider keys.", "body");

        var source = new MeshAgentSkillsSource(Mesh, contextPath: space);
        var skills = await Await(source, list => list.Any(s => s.Frontmatter.Name == "provider-keys"));

        skills.Select(s => s.Frontmatter.Name).Should().Contain("provider-keys",
            "MAF's grammar is lowercase alphanumerics separated by single hyphens; a mesh id that "
            + "does not match is folded rather than dropped");
    }

    // NOTE: there is deliberately no "seed a platform skill" test here. The built-in `Skill`
    // partition is served READ-ONLY (BuiltInSkillProvider behind a StaticNodePartitionStorageProvider),
    // so a test cannot create one — and shouldn't: that the platform layer is always queried is a
    // property of the query set, covered pure and fast by AgentSkillSubtreeQueriesTest.
}
