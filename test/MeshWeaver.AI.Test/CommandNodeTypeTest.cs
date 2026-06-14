#pragma warning disable CS1591

using System.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Commands AS mesh nodes — the data layer the chat autocomplete + execution read:
/// the built-in catalog (<see cref="BuiltInCommandProvider"/>), the inheritance query templates
/// (<see cref="CommandNodeType.CommandQueries"/>), the projection (<see cref="CommandNodeType.ProjectCommands"/>),
/// and the picker mapping (<see cref="CommandInfo.ToPickerRequest"/>). Pure POCO units.
/// </summary>
public class CommandNodeTypeTest
{
    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void BuiltInCommandProvider_ShipsStandardCommands_AsCommandNodes()
    {
        var nodes = new BuiltInCommandProvider().GetStaticNodes().ToList();

        nodes.Should().OnlyContain(n => n.NodeType == CommandNodeType.NodeType);
        nodes.Should().OnlyContain(n => n.Namespace == CommandNodeType.RootNamespace);
        nodes.Select(n => n.Id).OrderBy(x => x).Should().Equal("agent", "harness", "model");

        var def = (CommandDefinition)nodes.Single(n => n.Id == "model").Content!;
        def.Query.Should().Be("namespace:_Provider nodeType:LanguageModel scope:descendants");
        def.ComposerField.Should().Be("modelName");
        def.Title.Should().Be("Choose a model");
    }

    [Fact]
    public void ProjectCommands_DedupesById_NearerContextOverridesGlobal()
    {
        // Global /model then a Space-defined /model — the later (nearer-in-query-order) wins by id.
        var global = CommandNode("model", "global", "namespace:_Provider nodeType:LanguageModel", "modelName", "Choose a model");
        var spaceOverride = CommandNode("model", "space", "namespace:Acme/Models nodeType:LanguageModel", "modelName", "Acme models");

        var projected = CommandNodeType.ProjectCommands(new[] { global, spaceOverride }, Json);

        projected.Should().ContainSingle();
        projected[0].Id.Should().Be("model");
        projected[0].Definition.Query.Should().Be("namespace:Acme/Models nodeType:LanguageModel");
    }

    [Fact]
    public void ProjectCommands_ReadsJsonElementContent()
    {
        // Cross-hub query content arrives as JsonElement — the projection must still read it.
        var raw = JsonSerializer.SerializeToElement(
            new CommandDefinition { Query = "nodeType:Space", ComposerField = "contextPath", Title = "Choose a Space" }, Json);
        var node = new MeshNode("space", CommandNodeType.RootNamespace)
        {
            NodeType = CommandNodeType.NodeType, Description = "Pick a Space", Content = raw
        };

        var projected = CommandNodeType.ProjectCommands(new[] { node }, Json);

        projected.Should().ContainSingle();
        projected[0].Id.Should().Be("space");
        projected[0].Definition.ComposerField.Should().Be("contextPath");
    }

    [Fact]
    public void CommandInfo_ToPickerRequest_CarriesSpecAndSearchTerm()
    {
        var info = CommandNodeType.ProjectCommands(
            new[] { CommandNode("agent", "switch", "namespace:Agent nodeType:Agent", "agentName", "Choose an agent") }, Json)
            .Single();

        var req = info.ToPickerRequest("Worker");

        req.Query.Should().Be("namespace:Agent nodeType:Agent");
        req.ComposerField.Should().Be("agentName");
        req.Title.Should().Be("Choose an agent");
        req.SearchTerm.Should().Be("Worker");
    }

    [Fact]
    public void CommandQueries_IncludeGlobalCatalog_AndInheritedScopes()
    {
        var queries = CommandNodeType.CommandQueries(contextPath: "Acme/Project", userPath: "rbuergi");

        queries.Should().Contain("namespace:Command nodeType:Command");
        queries.Should().Contain("path:Acme/Project nodeType:Command scope:selfAndAncestors");
        queries.Should().Contain("path:rbuergi nodeType:Command scope:selfAndAncestors");

        // No context / user → only the global catalog.
        CommandNodeType.CommandQueries(null, null).Should().ContainSingle()
            .Which.Should().Be("namespace:Command nodeType:Command");
    }

    private static MeshNode CommandNode(string id, string desc, string query, string field, string title) =>
        new(id, CommandNodeType.RootNamespace)
        {
            NodeType = CommandNodeType.NodeType,
            Description = desc,
            Content = new CommandDefinition { Query = query, ComposerField = field, Title = title }
        };
}
