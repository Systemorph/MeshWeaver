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
    public void ExtractTypeName_ExtractsRecordName()
    {
        // Arrange
        var typeSource = @"
public record Story
{
    public string Id { get; init; }
    public string Title { get; init; }
}";

        // Act
        var typeName = _generator.ExtractTypeName(typeSource);

        // Assert
        typeName.Should().Be("Story");
    }

    [Fact]
    public void ExtractTypeName_ExtractsClassName()
    {
        // Arrange
        var typeSource = "public class MyClass { }";

        // Act
        var typeName = _generator.ExtractTypeName(typeSource);

        // Assert
        typeName.Should().Be("MyClass");
    }

    [Fact]
    public void ExtractTypeName_ExtractsSealedRecordName()
    {
        // Arrange
        var typeSource = "public sealed record SealedRecord { }";

        // Act
        var typeName = _generator.ExtractTypeName(typeSource);

        // Assert
        typeName.Should().Be("SealedRecord");
    }

    [Fact]
    public void ExtractTypeName_ReturnsDefaultForNoMatch()
    {
        // Arrange
        var typeSource = "// No type definition here";

        // Act
        var typeName = _generator.ExtractTypeName(typeSource);

        // Assert
        typeName.Should().Be("DynamicType");
    }

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
    public void GenerateAttributeSource_IncludesDataModelTypeSource()
    {
        // Arrange
        var node = new MeshNode("test/node")
        {
            Name = "Test Node",
            NodeType = "story",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "story",
            DisplayName = "Story",
            TypeSource = "public record Story { public string Title { get; init; } }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

        // Assert
        source.Should().Contain("public record Story");
        source.Should().Contain("public string Title");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesMeshNodeProperties()
    {
        // Arrange
        var node = new MeshNode("org/acme")
        {
            Name = "Acme Corp",
            NodeType = "organization",
            Description = "Test organization",
            IconName = "Building",
            DisplayOrder = 10,
            IsPersistent = true,
            LastModified = DateTimeOffset.Parse("2024-01-15T10:30:00Z")
        };

        var dataModel = new DataModel
        {
            Id = "organization",
            DisplayName = "Organization",
            TypeSource = "public record Organization { public string Name { get; init; } }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

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
        var node = new MeshNode("graph/org/project")
        {
            NodeType = "project",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "project",
            TypeSource = "public record Project { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

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

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

        // Assert
        source.Should().Contain("HubConfiguration = ConfigureHub");
        source.Should().Contain("private static MessageHubConfiguration ConfigureHub");
        source.Should().Contain("config.ConfigureMeshHub()");
        source.Should().Contain("WithDataType(typeof(MeshWeaver.Graph.Dynamic.TestType))");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesDynamicAreas_WhenEnabled()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        var hubFeatures = new HubFeatureConfig
        {
            Id = "test-features",
            EnableDynamicNodeTypeAreas = true
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, hubFeatures);

        // Assert
        source.Should().Contain("AddDynamicNodeTypeAreas()");
    }

    [Fact]
    public void GenerateAttributeSource_ExcludesDynamicAreas_WhenDisabled()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        var hubFeatures = new HubFeatureConfig
        {
            Id = "test-features",
            EnableDynamicNodeTypeAreas = false
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, hubFeatures);

        // Assert
        source.Should().Contain("// Dynamic areas disabled");
        source.Should().NotContain("AddDynamicNodeTypeAreas()");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesNodeTypeConfiguration()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "story",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "story",
            DisplayName = "Story",
            Description = "A story item",
            IconName = "Document",
            DisplayOrder = 5,
            TypeSource = "public record Story { public string Title { get; init; } }"
        };

        var nodeTypeConfig = new NodeTypeConfig
        {
            NodeType = "story",
            DataModelId = "story",
            DisplayName = "Custom Story",
            DisplayOrder = 10
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, nodeTypeConfig, null);

        // Assert
        source.Should().Contain("NodeTypeConfiguration");
        source.Should().Contain("NodeType = \"story\"");
        source.Should().Contain("DisplayName = \"Custom Story\""); // From NodeTypeConfig override
        source.Should().Contain("DisplayOrder = 10"); // From NodeTypeConfig override
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

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

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

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

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
        var node = new MeshNode("my/node")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

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

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

        // Assert
        source.Should().Contain("AssemblyLocation = typeof(");
        source.Should().Contain(").Assembly.Location");
    }

    [Fact]
    public void GenerateAttributeSource_IncludesDefaultNodeViews()
    {
        // Arrange
        var node = new MeshNode("test")
        {
            NodeType = "test",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "test",
            TypeSource = "public record TestType { }"
        };

        // Act
        var source = _generator.GenerateAttributeSource(node, dataModel, null, null);

        // Assert - must include default views for Details, Edit, etc.
        source.Should().Contain("WithDefaultNodeViews()",
            "Generated code must include default views (Details, Edit, Thumbnail, Metadata, Settings, Comments)");
    }
}
