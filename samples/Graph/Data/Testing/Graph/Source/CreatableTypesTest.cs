// <meshweaver>
// Id: Testing/Graph/CreatableTypesTest
// DisplayName: Testing/Graph/CreatableTypesTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

/// <summary>
/// Tests for CreatableTypesRules and the fluent API.
/// </summary>
public class CreatableTypesTest
{
    #region CreatableTypesRules Tests

    [MeshFact]
    public void CreatableTypesRules_Default_IncludesDefaults()
    {
        // Arrange
        var rules = new CreatableTypesRules();
        var defaults = new[] { "Markdown", "Thread", "Agent", "NodeType" };

        // Act
        var types = rules.GetCreatableTypes(null, defaults).ToList();

        // Assert
        types.Should().Contain("Markdown");
        types.Should().Contain("Thread");
        types.Should().Contain("Agent");
        types.Should().Contain("NodeType");
    }

    [MeshFact]
    public void CreatableTypesRules_ClearDefaults_ExcludesDefaults()
    {
        // Arrange
        var rules = new CreatableTypesRules { IncludeDefaults = false };
        var defaults = new[] { "Markdown", "Thread", "Agent", "NodeType" };

        // Act
        var types = rules.GetCreatableTypes(null, defaults).ToList();

        // Assert
        types.Should().BeEmpty();
    }

    [MeshFact]
    public void CreatableTypesRules_AddRule_IncludesTypes()
    {
        // Arrange
        var rules = new CreatableTypesRules { IncludeDefaults = false }
            .Add(_ => new[] { "ACME/Project", "ACME/Task" });

        // Act
        var types = rules.GetCreatableTypes(null, []).ToList();

        // Assert
        types.Should().Contain("ACME/Project");
        types.Should().Contain("ACME/Task");
    }

    [MeshFact]
    public void CreatableTypesRules_MultipleRules_Accumulate()
    {
        // Arrange
        var rules = new CreatableTypesRules { IncludeDefaults = false }
            .Add(_ => new[] { "Type1" })
            .Add(_ => new[] { "Type2" });

        // Act
        var types = rules.GetCreatableTypes(null, []).ToList();

        // Assert
        types.Should().HaveCount(2);
        types.Should().Contain("Type1");
        types.Should().Contain("Type2");
    }

    [MeshFact]
    public void CreatableTypesRules_ExcludedTypes_AreRemoved()
    {
        // Arrange
        var rules = new CreatableTypesRules
        {
            IncludeDefaults = true,
            ExcludedTypes = ["Markdown", "Agent"]
        };
        var defaults = new[] { "Markdown", "Thread", "Agent", "NodeType" };

        // Act
        var types = rules.GetCreatableTypes(null, defaults).ToList();

        // Assert
        types.Should().NotContain("Markdown");
        types.Should().NotContain("Agent");
        types.Should().Contain("Thread");
        types.Should().Contain("NodeType");
    }

    [MeshFact]
    public void CreatableTypesRules_RuleCanUseParentNode()
    {
        // Arrange
        var parentNode = new MeshNode("ACME/ProjectA")
        {
            NodeType = "ACME/Project"
        };

        var rules = new CreatableTypesRules { IncludeDefaults = false }
            .Add(parent => parent?.NodeType == "ACME/Project"
                ? new[] { "ACME/Project/Todo" }
                : Array.Empty<string>());

        // Act - with matching parent
        var typesWithParent = rules.GetCreatableTypes(parentNode, []).ToList();

        // Act - with null parent
        var typesWithNull = rules.GetCreatableTypes(null, []).ToList();

        // Assert
        typesWithParent.Should().Contain("ACME/Project/Todo");
        typesWithNull.Should().BeEmpty();
    }

    [MeshFact]
    public void CreatableTypesRules_DuplicatesAreRemoved()
    {
        // Arrange
        var rules = new CreatableTypesRules { IncludeDefaults = false }
            .Add(_ => new[] { "Type1", "Type1", "Type2" })
            .Add(_ => new[] { "Type1" }); // duplicate from another rule

        // Act
        var types = rules.GetCreatableTypes(null, []).ToList();

        // Assert
        types.Should().HaveCount(2);
        types.Should().Contain("Type1");
        types.Should().Contain("Type2");
    }

    #endregion

    #region NodeTypeDefinition Tests

    [MeshFact]
    public void NodeTypeDefinition_CreatableTypes_CanBeSet()
    {
        // Arrange & Act
        var definition = new NodeTypeDefinition
        {
            CreatableTypes = ["ACME/Project/Todo", "ACME/Project/Story"]
        };

        // Assert
        definition.CreatableTypes.Should().HaveCount(2);
        definition.CreatableTypes.Should().Contain("ACME/Project/Todo");
    }

    [MeshFact]
    public void NodeTypeDefinition_IncludeGlobalTypes_DefaultsToTrue()
    {
        // Arrange & Act
        var definition = new NodeTypeDefinition { };

        // Assert
        definition.IncludeGlobalTypes.Should().BeTrue();
    }

    [MeshFact]
    public void NodeTypeDefinition_IncludeGlobalTypes_CanBeDisabled()
    {
        // Arrange & Act
        var definition = new NodeTypeDefinition
        {
            IncludeGlobalTypes = false
        };

        // Assert
        definition.IncludeGlobalTypes.Should().BeFalse();
    }

    #endregion
}
