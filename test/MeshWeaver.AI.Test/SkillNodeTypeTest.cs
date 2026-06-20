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
    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void BuiltInSkillProvider_ShipsStandardSkills_AsPickSkillNodes()
    {
        var nodes = new BuiltInSkillProvider().GetStaticNodes().ToList();

        // The catalog also ships the partition PublicRead _Policy (so the synced partition is readable).
        nodes.Should().Contain(n => n.NodeType == "PartitionAccessPolicy" && n.Id == "_Policy");

        var skills = nodes.Where(n => n.NodeType == SkillNodeType.NodeType).ToList();
        skills.Should().OnlyContain(n => n.Namespace == SkillNodeType.RootNamespace);
        skills.Select(n => n.Id).OrderBy(x => x).Should().Equal("agent", "harness", "model");

        var def = (SkillDefinition)skills.Single(n => n.Id == "model").Content!;
        def.Action!.Kind.Should().Be(SkillActionKind.Pick);
        def.Action.Query.Should().Be("namespace:_Provider nodeType:LanguageModel scope:descendants sort:order");
        def.Action.Field.Should().Be("modelName");
        def.Action.Title.Should().Be("Choose a model");
    }

    [Fact]
    public void ProjectSkills_DedupesById_NearerContextOverridesGlobal()
    {
        // Global /model then a Space-defined /model — the later (nearer-in-query-order) wins by id.
        var global = SkillNode("model", "global", "namespace:_Provider nodeType:LanguageModel", "modelName", "Choose a model");
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
        // skill is dropped — the live memex/atioz bug. Verify the omitted default round-trips to Pick.
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
    public void SkillQueries_AreTheUnifiedRegistryPattern_PlatformPlusSpacePlusUser()
    {
        // Same shape as agents + models: ONE namespace:A|B|C exact-membership query (platform Skill +
        // the space's {space}/Skill + the user's {user}/Skill).
        SkillNodeType.SkillQueries(contextPath: "Acme/Project", userPath: "rbuergi")
            .Should().ContainSingle()
            .Which.Should().Be("namespace:rbuergi/Skill|Acme/Skill|Skill nodeType:Skill");

        // No context / user → platform defaults only.
        SkillNodeType.SkillQueries(null, null).Should().ContainSingle()
            .Which.Should().Be("namespace:Skill nodeType:Skill");
    }

    [Fact]
    public void SkillQueries_SkipRogueReservedContextPartition()
    {
        // A rogue/reserved ROUTE context (e.g. "login" — an auto-minted page artifact) must NOT be added
        // to the namespace IN(...): it has no read policy, so including "login/Skill" fails the WHOLE
        // query ("lacks Read permission on 'login'") and the picker/autocomplete goes empty.
        var q = SkillNodeType.SkillQueries(contextPath: "login", userPath: "rbuergi").Single();
        q.Should().Be("namespace:rbuergi/Skill|Skill nodeType:Skill");
        q.Should().NotContain("login/Skill");
    }

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
