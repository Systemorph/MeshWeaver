using System;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for CreatableTypesRules and the fluent API.
/// </summary>
public class CreatableTypesTest
{
    #region CreatableTypesRules Tests

    [Fact]
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

    [Fact]
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

    [Fact]
    public void CreatableTypesRules_AddRule_IncludesTypes()
    {
        // Arrange
        var rules = new CreatableTypesRules { IncludeDefaults = false }
            .Add(_ => new[] { "Demos/ACME/Project", "Demos/ACME/Task" });

        // Act
        var types = rules.GetCreatableTypes(null, []).ToList();

        // Assert
        types.Should().Contain("Demos/ACME/Project");
        types.Should().Contain("Demos/ACME/Task");
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public void CreatableTypesRules_RuleCanUseParentNode()
    {
        // Arrange
        var parentNode = new MeshNode("Demos/ACME/ProjectA")
        {
            NodeType = "Demos/ACME/Project"
        };

        var rules = new CreatableTypesRules { IncludeDefaults = false }
            .Add(parent => parent?.NodeType == "Demos/ACME/Project"
                ? new[] { "Demos/ACME/Project/Todo" }
                : Array.Empty<string>());

        // Act - with matching parent
        var typesWithParent = rules.GetCreatableTypes(parentNode, []).ToList();

        // Act - with null parent
        var typesWithNull = rules.GetCreatableTypes(null, []).ToList();

        // Assert
        typesWithParent.Should().Contain("Demos/ACME/Project/Todo");
        typesWithNull.Should().BeEmpty();
    }

    [Fact]
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

    [Fact]
    public void NodeTypeDefinition_CreatableTypes_CanBeSet()
    {
        // Arrange & Act
        var definition = new NodeTypeDefinition
        {
            CreatableTypes = ["Demos/ACME/Project/Todo", "Demos/ACME/Project/Story"]
        };

        // Assert
        definition.CreatableTypes.Should().HaveCount(2);
        definition.CreatableTypes.Should().Contain("Demos/ACME/Project/Todo");
    }

    [Fact]
    public void NodeTypeDefinition_IncludeGlobalTypes_DefaultsToTrue()
    {
        // Arrange & Act
        var definition = new NodeTypeDefinition { };

        // Assert
        definition.IncludeGlobalTypes.Should().BeTrue();
    }

    [Fact]
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
