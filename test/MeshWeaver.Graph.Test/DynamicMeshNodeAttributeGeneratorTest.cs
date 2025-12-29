using System;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for DynamicMeshNodeAttributeGenerator - generates C# source for MeshNodeAttribute.
/// </summary>
public class DynamicMeshNodeAttributeGeneratorTest
{
    private readonly DynamicMeshNodeAttributeGenerator _generator = new();

    [Fact]
    public void SanitizeName_ReplacesSlashes()
    {
        // Arrange
        var path = "graph/org/project";

        // Act
        var sanitized = _generator.SanitizeName(path);

        // Assert
        sanitized.Should().Be("graph_org_project");
    }

    [Fact]
    public void SanitizeName_RemovesSpecialCharacters()
    {
        // Arrange
        var path = "my-path.with.dots";

        // Act
        var sanitized = _generator.SanitizeName(path);

        // Assert
        sanitized.Should().NotContain("-");
        sanitized.Should().NotContain(".");
    }

    [Fact]
    public void SanitizeName_EnsuresStartsWithLetter()
    {
        // Arrange
        var path = "123numeric";

        // Act
        var sanitized = _generator.SanitizeName(path);

        // Assert
        char.IsLetter(sanitized[0]).Should().BeTrue();
    }

    [Fact]
    public void GenerateAttributeSource_IncludesCodeFromCodeConfiguration()
    {
        // Arrange
        var node = MeshNode.FromPath("test/node") with
        {
            Name = "Test Node",
            NodeType = "story",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record Story { public string Title { get; init; } }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("public record Story");
        source.Should().Contain("public string Title");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesMeshNodeProperties()
    {
        // Arrange
        var node = MeshNode.FromPath("org/acme") with
        {
            Name = "Acme Corp",
            NodeType = "organization",
            Description = "Test organization",
            IconName = "Building",
            DisplayOrder = 10,
            IsPersistent = true,
            LastModified = DateTimeOffset.Parse("2024-01-15T10:30:00Z")
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record Organization { public string Name { get; init; } }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("Name = \"Acme Corp\"");
        source.Should().Contain("NodeType = \"organization\"");
        source.Should().Contain("Description = \"Test organization\"");
        source.Should().Contain("IconName = \"Building\"");
        source.Should().Contain("DisplayOrder = 10");
        source.Should().Contain("IsPersistent = true");
    }

    [Fact]
    public void GenerateAttributeSource_GeneratesValidClassName()
    {
        // Arrange
        var node = MeshNode.FromPath("graph/org/project") with
        {
            NodeType = "project",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record Project { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("class graph_org_projectMeshNodeAttribute");
        source.Should().Contain("[assembly: MeshWeaver.Graph.Generated.graph_org_projectMeshNode]");

        // Verify assembly attribute comes before namespaces
        var assemblyAttrIndex = source.IndexOf("[assembly:");
        var namespaceIndex = source.IndexOf("namespace MeshWeaver.Graph.Dynamic");
        assemblyAttrIndex.Should().BeLessThan(namespaceIndex, "Assembly attribute must come before namespace declarations");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesHubConfiguration()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("HubConfiguration = ConfigureHub");
        source.Should().Contain("private static MessageHubConfiguration ConfigureHub");
    }

    [Fact]
    public void GenerateAttributeSource_EscapesSpecialCharacters()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            Name = "Test \"quoted\" name",
            Description = "Line1\nLine2",
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("\\\"quoted\\\"");
        source.Should().Contain("\\n");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesRequiredUsings()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("using System;");
        source.Should().Contain("using MeshWeaver.Mesh;");
        source.Should().Contain("using MeshWeaver.Messaging;");
        source.Should().Contain("using System.ComponentModel.DataAnnotations;");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesGeneratedComment()
    {
        // Arrange
        var node = MeshNode.FromPath("my/node") with
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("// Auto-generated from MeshNode: my/node");
        source.Should().Contain("// Generated at:");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesAssemblyLocation()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert
        source.Should().Contain("AssemblyLocation = typeof(");
        source.Should().Contain(").Assembly.Location");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesDefaultViews_ForNonNodeTypeNodes()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test", // not "NodeType"
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert - WithDefaultViews() is injected automatically for non-NodeType nodes
        source.Should().Contain("WithDefaultViews()",
            "Generated code must include WithDefaultViews() for non-NodeType nodes");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesDefaultViews_ForNodeTypeNodes()
    {
        // Arrange - generator checks node.Content is NodeTypeDefinition, NOT node.NodeType
        var node = new MeshNode("Type/Test")
        {
            NodeType = "NodeType",
            LastModified = DateTimeOffset.UtcNow,
            Content = new NodeTypeDefinition { Id = "Test", Namespace = "Type" }
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert - NodeType nodes MUST include WithDefaultViews() because the same ConfigureHub is used
        // for both the type definition and instances of that type. Instances need standard views (Details, etc.)
        source.Should().Contain("ConfigureMeshHub().WithCodeConfiguration().Build().WithDefaultViews()",
            "Generated code must include ConfigureMeshHub with WithDefaultViews for NodeType definition nodes");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesCustomHubConfiguration_WhenProvided()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = "public record TestType { }"
        };

        var hubConfiguration = "config => config.AddData(d => d.AddSource(s => s.WithType<TestType>()))";

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, hubConfiguration);

        // Assert
        source.Should().Contain(hubConfiguration);
    }

    [Fact]
    public void GenerateAttributeSource_HandlesNullCodeConfiguration()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, null, null);

        // Assert - should still generate valid code
        source.Should().Contain("class testMeshNodeAttribute");
        source.Should().Contain("HubConfiguration = ConfigureHub");
    }

    [Fact]
    public void GenerateAttributeSource_HandlesEmptyCode()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var codeConfig = new CodeConfiguration
        {
            Code = ""
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, codeConfig, null);

        // Assert - should still generate valid code
        source.Should().Contain("class testMeshNodeAttribute");
    }
}
