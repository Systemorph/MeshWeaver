using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Pivot.Models;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Pivot.Test;

/// <summary>
/// Tests for ColumnGroup serialization behavior to ensure polymorphic serialization 
/// works correctly and the Children property is preserved during serialization/deserialization.
/// </summary>
public class ColumnGroupSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    JsonSerializerOptions Options => GetHost().JsonSerializerOptions; [Fact]
    public void ColumnGroup_SerializesWithCorrectTypeDiscriminator()
    {
        // Arrange
        var childColumn1 = new Column("child1", "Child Column 1");
        var childColumn2 = new Column("child2", "Child Column 2");

        var columnGroup = new ColumnGroup("group1", "Group Column", "grouper")
            .AddChildren(new[] { childColumn1, childColumn2 });

        // Act
        var json = JsonSerializer.Serialize(columnGroup, Options);

        // Debug output
        Output.WriteLine($"Direct ColumnGroup JSON: {json}");

        // Assert
        json.Should().Contain("MeshWeaver.Pivot.Models.ColumnGroup",
            "ColumnGroup should be serialized with correct type discriminator");
        json.Should().Contain("children",
            "ColumnGroup should include the Children property");
        json.Should().Contain("child1",
            "Children collection should contain child column ids");
        json.Should().Contain("child2",
            "Children collection should contain all child column ids");
    }

    [Fact]
    public void ColumnGroup_DeserializesWithChildren()
    {
        // Arrange
        var childColumn1 = new Column("child1", "Child Column 1");
        var childColumn2 = new Column("child2", "Child Column 2");

        var originalColumnGroup = new ColumnGroup("group1", "Group Column", "grouper")
            .AddChildren(new[] { childColumn1, childColumn2 });

        // Act
        var json = JsonSerializer.Serialize(originalColumnGroup, Options);
        var deserializedColumnGroup = JsonSerializer.Deserialize<ColumnGroup>(json, Options);

        // Assert
        deserializedColumnGroup.Should().NotBeNull();
        deserializedColumnGroup.Id.Should().Be("group1");
        deserializedColumnGroup.DisplayName.Should().Be("Group Column");
        deserializedColumnGroup.Children.Should().HaveCount(2, "ColumnGroup should preserve its children");
        deserializedColumnGroup.Children.First().Id.Should().Be("child1");
        deserializedColumnGroup.Children.Last().Id.Should().Be("child2");
    }
    [Fact]
    public void PivotModel_SerializesColumnGroupWithChildren()
    {
        // Arrange
        var childColumn1 = new Column("child1", "Child Column 1");
        var childColumn2 = new Column("child2", "Child Column 2");

        var columnGroup = new ColumnGroup("group1", "Group Column", "grouper")
            .AddChildren(new[] { childColumn1, childColumn2 });

        var columns = new List<Column> { columnGroup };
        var rows = new List<Row>(); // Empty rows for this test
        var pivotModel = new PivotModel(columns, rows);

        // Act: Serialize the model
        var json = JsonSerializer.Serialize(pivotModel, Options);

        // Debug output
        Output.WriteLine($"PivotModel with ColumnGroup JSON: {json}");

        // Assert: Verify the serialized JSON contains the expected structure
        // The JSON should include ColumnGroup type information and Children property
        json.Should().Contain("MeshWeaver.Pivot.Models.ColumnGroup",
            "ColumnGroup should be serialized with correct type discriminator");
        json.Should().Contain("children",
            "ColumnGroup should include the Children property");
        json.Should().Contain("child1",
            "Children collection should contain child column ids");
        json.Should().Contain("child2",
            "Children collection should contain all child column ids");
    }

    [Fact]
    public void PivotModel_RoundTripSerializationPreservesColumnGroupChildren()
    {
        // Arrange
        var childColumn1 = new Column("child1", "Child Column 1");
        var childColumn2 = new Column("child2", "Child Column 2");

        var columnGroup = new ColumnGroup("group1", "Group Column", "grouper")
            .AddChildren(new[] { childColumn1, childColumn2 });

        var columns = new List<Column> { columnGroup };
        var rows = new List<Row>(); // Empty rows for this test
        var originalModel = new PivotModel(columns, rows);

        // Act
        var json = JsonSerializer.Serialize(originalModel, Options);
        var deserializedModel = JsonSerializer.Deserialize<PivotModel>(json, Options);

        // Assert
        deserializedModel.Should().NotBeNull();
        deserializedModel.Columns.Should().HaveCount(1);

        var deserializedGroup = deserializedModel.Columns.First() as ColumnGroup;
        deserializedGroup.Should().NotBeNull("Deserialized column should be a ColumnGroup");
        deserializedGroup.Id.Should().Be("group1");
        deserializedGroup.DisplayName.Should().Be("Group Column");
        deserializedGroup.Children.Should().HaveCount(2, "ColumnGroup should preserve its children");
        deserializedGroup.Children.First().Id.Should().Be("child1");
        deserializedGroup.Children.Last().Id.Should().Be("child2");
    }

    [Fact]
    public void ColumnGroup_EmptyChildren_SerializesCorrectly()
    {
        // Arrange
        var columnGroup = new ColumnGroup("group1", "Empty Group", "grouper");

        // Act
        var json = JsonSerializer.Serialize(columnGroup, Options);
        var deserialized = JsonSerializer.Deserialize<ColumnGroup>(json, Options);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Id.Should().Be("group1");
        deserialized.DisplayName.Should().Be("Empty Group");
        deserialized.Children.Should().BeEmpty();
    }

    [Fact]
    public void ColumnGroup_NestedColumnGroups_SerializesCorrectly()
    {
        // Arrange
        var leafColumn = new Column("leaf", "Leaf Column");
        var nestedGroup = new ColumnGroup("nested", "Nested Group", "nested-grouper")
            .AddChildren(new[] { leafColumn });

        var parentGroup = new ColumnGroup("parent", "Parent Group", "parent-grouper")
            .AddChildren(new[] { nestedGroup });

        // Act
        var json = JsonSerializer.Serialize(parentGroup, Options);
        var deserialized = JsonSerializer.Deserialize<ColumnGroup>(json, Options);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Id.Should().Be("parent");
        deserialized.Children.Should().HaveCount(1);

        var deserializedNested = deserialized.Children.First() as ColumnGroup;
        deserializedNested.Should().NotBeNull("Nested child should be a ColumnGroup");
        deserializedNested.Id.Should().Be("nested");
        deserializedNested.Children.Should().HaveCount(1);
        deserializedNested.Children.First().Id.Should().Be("leaf");
    }
}
