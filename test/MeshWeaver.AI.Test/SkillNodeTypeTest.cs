#pragma warning disable CS1591

using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Skills AS mesh nodes — the unified slash-skill data layer (subsumes the retired Command tests):
/// the built-in catalog (<see cref="BuiltInSkillProvider"/>), the inheritance query templates
/// (<see cref="SkillNodeType.SkillQueries"/>), the projection (<see cref="SkillNodeType.ProjectSkills"/>),
/// and the picker mapping (<see cref="SkillInfo.ToPickerRequest"/>) for a <see cref="SkillActionKind.Pick"/>.
/// </summary>
public class SkillNodeTypeTest
{
    /// <summary>The projection every registry query carries — referenced from the
    /// production constant so these expectations can never drift from it. Asserting it
    /// here is the point: a registry query that stops projecting `content` yields nodes
    /// whose Content is silently null, which reads as an empty picker.</summary>
    private const string Proj = AgentPickerProjection.RegistryProjection;

    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void BuiltInSkillProvider_ShipsStandardSkills_AsPickSkillNodes()
    {
        var nodes = new BuiltInSkillProvider().GetStaticNodes().ToList();

        // The catalog also ships the partition PublicRead _Policy (so the synced partition is readable).
        nodes.Should().Contain(n => n.NodeType == "PartitionAccessPolicy" && n.Id == "_Policy");

        var skills = nodes.Where(n => n.NodeType == SkillNodeType.NodeType).ToList();
        skills.Should().OnlyContain(n => n.Namespace == SkillNodeType.RootNamespace);
        skills.Select(n => n.Id).OrderBy(x => x).Should().Equal(BuiltInSkillCatalog.ExpectedIds);

        var def = (SkillDefinition)skills.Single(n => n.Id == "model").Content!;
        def.Action!.Kind.Should().Be(SkillActionKind.Pick);
        def.Action.Query.Should().Be("namespace:Provider nodeType:LanguageModel scope:descendants sort:order");
        def.Action.Field.Should().Be("modelName");
        def.Action.Title.Should().Be("Choose a model");

        // /navigate is a Navigate-action skill — the pane-aware, resilient "take me there".
        var nav = (SkillDefinition)skills.Single(n => n.Id == "navigate").Content!;
        nav.Action!.Kind.Should().Be(SkillActionKind.Navigate);

        // /clear is a NewThread-action skill — replaces the side panel with a fresh new-chat composer.
        var clear = (SkillDefinition)skills.Single(n => n.Id == "clear").Content!;
        clear.Action!.Kind.Should().Be(SkillActionKind.NewThread);
    }

    [Fact]
    public void ProjectSkills_DedupesById_NearerContextOverridesGlobal()
    {
        // Global /model then a Space-defined /model — the later (nearer-in-query-order) wins by id.
        var global = SkillNode("model", "global", "namespace:Provider nodeType:LanguageModel", "modelName", "Choose a model");
        var spaceOverride = SkillNode("model", "space", "namespace:Acme/Models nodeType:LanguageModel", "modelName", "Acme models");

        var projected = SkillNodeType.ProjectSkills(new[] { global, spaceOverride }, Json);

        projected.Should().ContainSingle();
        projected[0].Id.Should().Be("model");
        projected[0].Definition.Action!.Query.Should().Be("namespace:Acme/Models nodeType:LanguageModel");
    }

    [Fact]
    public void ProjectSkills_ReadsJsonElementContent()
    {
        // Cross-hub query content arrives as JsonElement — the projection must still read it.
        var raw = JsonSerializer.SerializeToElement(
            new SkillDefinition
            {
                Action = new SkillAction { Kind = SkillActionKind.Pick, Query = "nodeType:Space", Field = "contextPath", Title = "Choose a Space" }
            }, Json);
        var node = new MeshNode("space", SkillNodeType.RootNamespace)
        {
            NodeType = SkillNodeType.NodeType, Description = "Pick a Space", Content = raw
        };

        var projected = SkillNodeType.ProjectSkills(new[] { node }, Json);

        projected.Should().ContainSingle();
        projected[0].Id.Should().Be("space");
        projected[0].Definition.Action!.Field.Should().Be("contextPath");
    }

    [Fact]
    public void SkillAction_RoundTrips_WhenKindOmittedAsDefault()
    {
        // The hub serializer omits default-valued properties (DefaultIgnoreCondition.WhenWritingDefault),
        // so a Pick action (Kind = the default enum value 0) serializes with NO `kind` field. A *required*
        // Kind then fails to deserialize ("missing required properties including: 'kind'") and EVERY Pick
        // skill is dropped — the live memex/prod bug. Verify the omitted default round-trips to Pick.
        var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
        var def = new SkillDefinition
        {
            Action = new SkillAction
            {
                Kind = SkillActionKind.Pick, Query = "namespace:Agent nodeType:Agent", Field = "agentName", Title = "Choose an agent"
            }
        };

        var json = JsonSerializer.Serialize(def, opts);
        json.Should().NotContain("kind", "Pick is the default enum value, so the serializer omits it");

        var roundTripped = JsonSerializer.Deserialize<SkillDefinition>(json, opts);
        roundTripped!.Action!.Kind.Should().Be(SkillActionKind.Pick);
        roundTripped.Action.Field.Should().Be("agentName");
    }

    [Fact]
    public void SkillInfo_ToPickerRequest_CarriesSpecAndSearchTerm()
    {
        var info = SkillNodeType.ProjectSkills(
            new[] { SkillNode("agent", "switch", "namespace:Agent nodeType:Agent", "agentName", "Choose an agent") }, Json)
            .Single();

        var req = info.ToPickerRequest("Worker");

        req.Should().NotBeNull();
        req!.Query.Should().Be("namespace:Agent nodeType:Agent");
        req.ComposerField.Should().Be("agentName");
        req.Title.Should().Be("Choose an agent");
        req.SearchTerm.Should().Be("Worker");
    }

    [Fact]
    public void SkillInfo_ToSubmissionText_DigestsTypedTaskForInstructionSkill()
    {
        // "/code build a Todo NodeType …" — the text typed AFTER the skill word is the task. It is
        // digested into the submitted message together with a load_skill directive naming this skill.
        var info = SkillNodeType.ProjectSkills(new[] { InstructionSkillNode("code") }, Json).Single();

        var text = info.ToSubmissionText("  build a Todo NodeType with a Kanban board  ");

        text.Should().NotBeNull();
        text.Should().Contain("build a Todo NodeType with a Kanban board", "the typed task is digested verbatim");
        text.Should().Contain(info.Path!, "the round must know WHICH skill to load");
        text.Should().Contain("load_skill", "the agent loads the skill's instructions before starting");
        text.Should().NotStartWith("/", "the composed message must not re-parse as a slash command");
    }

    [Fact]
    public void SkillInfo_ToSubmissionText_NothingToDigest_ReturnsNull()
    {
        var instruction = SkillNodeType.ProjectSkills(new[] { InstructionSkillNode("code") }, Json).Single();
        instruction.ToSubmissionText(null).Should().BeNull("no task text — the chat shows the skill's help");
        instruction.ToSubmissionText("   ").Should().BeNull("whitespace is not a task");

        // Behaviour skills consume their argument themselves (the picker search term) — never a round.
        var pick = SkillNodeType.ProjectSkills(
            new[] { SkillNode("agent", "switch", "namespace:Agent nodeType:Agent", "agentName", "Choose an agent") }, Json)
            .Single();
        pick.ToSubmissionText("Worker").Should().BeNull("a Pick skill's argument pre-filters the picker");
    }

    [Fact]
    public void SkillQueries_AreTheUnifiedRegistryPattern_PlatformPlusSpacePlusUser()
    {
        // ONE layered query set, most specific first: the user's own flat {user}/Skill, the context
        // partition's whole SUBTREE, then the platform defaults. This is the same set the chat's
        // picker/autocomplete resolve AND the set MeshAgentSkillsSource feeds the agent framework.
        SkillNodeType.SkillQueries(contextPath: "Acme/Project", userPath: "rbuergi")
            .Should().Equal(
                "namespace:Skill nodeType:Skill" + Proj,
                "namespace:rbuergi/Skill nodeType:Skill" + Proj,
                "path:Acme scope:descendants nodeType:Skill" + Proj);

        // No context / user → platform defaults only.
        SkillNodeType.SkillQueries(null, null).Should().Equal("namespace:Skill nodeType:Skill" + Proj);
    }

    [Fact]
    public void SkillQueries_SkipRogueReservedContextPartition()
    {
        // A rogue/reserved ROUTE context (e.g. "login" — an auto-minted page artifact) must NOT
        // contribute a layer: it has no read policy, so querying it fails ("lacks Read permission on
        // 'login'") and the picker/autocomplete goes empty.
        var queries = SkillNodeType.SkillQueries(contextPath: "login", userPath: "rbuergi");
        queries.Should().Equal(
            "namespace:Skill nodeType:Skill" + Proj,
            "namespace:rbuergi/Skill nodeType:Skill" + Proj);
        string.Join(" ", queries).Should().NotContain("login");
    }

    private static MeshNode InstructionSkillNode(string id) =>
        new(id, SkillNodeType.RootNamespace)
        {
            NodeType = SkillNodeType.NodeType,
            Description = "Bring in coding capability",
            Content = new SkillDefinition { Instructions = "# Coding rules\n\nRead the architecture docs first." }
        };

    private static MeshNode SkillNode(string id, string desc, string query, string field, string title) =>
        new(id, SkillNodeType.RootNamespace)
        {
            NodeType = SkillNodeType.NodeType,
            Description = desc,
            Content = new SkillDefinition
            {
                Action = new SkillAction { Kind = SkillActionKind.Pick, Query = query, Field = field, Title = title }
            }
        };
}
