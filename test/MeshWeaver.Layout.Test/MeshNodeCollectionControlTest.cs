using FluentAssertions;
using MeshWeaver.Domain;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class MeshNodeCollectionControlTest
{
    [Fact]
    public void Control_DefaultProperties()
    {
        var control = new MeshNodeCollectionControl();

        control.Queries.Should().BeEmpty();
        control.Deletable.Should().BeFalse();
        control.ShowAdd.Should().BeTrue();
        control.AddDialogTitle.Should().BeNull();
        control.AddPickerQueries.Should().BeNull();
        control.AddPickerLabel.Should().BeNull();
    }

    [Fact]
    public void WithQueries_SetsMultipleQueries()
    {
        var control = new MeshNodeCollectionControl()
            .WithQueries("namespace:X nodeType:AccessAssignment", "path:Y nodeType:Group");

        control.Queries.Should().HaveCount(2);
        control.Queries[0].Should().Be("namespace:X nodeType:AccessAssignment");
        control.Queries[1].Should().Be("path:Y nodeType:Group");
    }

    [Fact]
    public void WithDeletable_SetsFlag()
    {
        var control = new MeshNodeCollectionControl()
            .WithDeletable(true);

        control.Deletable.Should().BeTrue();
    }

    [Fact]
    public void WithShowAdd_SetsFlag()
    {
        var control = new MeshNodeCollectionControl()
            .WithShowAdd(false);

        control.ShowAdd.Should().BeFalse();
    }

    [Fact]
    public void WithAddDialogTitle_SetsTitle()
    {
        var control = new MeshNodeCollectionControl()
            .WithAddDialogTitle("Add Assignment");

        control.AddDialogTitle.Should().Be("Add Assignment");
    }

    [Fact]
    public void WithAddPickerQueries_SetsMultipleQueries()
    {
        var control = new MeshNodeCollectionControl()
            .WithAddPickerQueries("namespace:User nodeType:User", "namespace:X nodeType:Group");

        control.AddPickerQueries.Should().HaveCount(2);
        control.AddPickerQueries![0].Should().Be("namespace:User nodeType:User");
        control.AddPickerQueries[1].Should().Be("namespace:X nodeType:Group");
    }

    [Fact]
    public void WithAddPickerLabel_SetsLabel()
    {
        var control = new MeshNodeCollectionControl()
            .WithAddPickerLabel("Subject (User or Group)");

        control.AddPickerLabel.Should().Be("Subject (User or Group)");
    }

    [Fact]
    public void FluentBuilders_Chain()
    {
        var control = new MeshNodeCollectionControl()
            .WithQueries("namespace:X nodeType:AccessAssignment")
            .WithDeletable(true)
            .WithShowAdd(true)
            .WithAddDialogTitle("Add Item")
            .WithAddPickerQueries("namespace:User nodeType:User")
            .WithAddPickerLabel("Choose item");

        control.Queries.Should().HaveCount(1);
        control.Deletable.Should().BeTrue();
        control.ShowAdd.Should().BeTrue();
        control.AddDialogTitle.Should().Be("Add Item");
        control.AddPickerQueries.Should().HaveCount(1);
        control.AddPickerLabel.Should().Be("Choose item");
    }

    [Fact]
    public void Attribute_StoresQueries()
    {
        var attr = new MeshNodeCollectionAttribute(
            "namespace:{node.namespace} nodeType:Role scope:selfAndAncestors"
        );

        attr.Queries.Should().HaveCount(1);
        attr.Queries[0].Should().Be("namespace:{node.namespace} nodeType:Role scope:selfAndAncestors");
    }

    [Fact]
    public void Attribute_MultipleQueries()
    {
        var attr = new MeshNodeCollectionAttribute(
            "namespace:User nodeType:User",
            "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors"
        );

        attr.Queries.Should().HaveCount(2);
    }

    [Fact]
    public void Attribute_ResolveQueries_ResolvesTemplateVariables()
    {
        var queries = new[] { "namespace:{node.namespace} nodeType:Role scope:selfAndAncestors" };

        var resolved = MeshNodeCollectionAttribute.ResolveQueries(queries, "ACME/Project");

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("namespace:ACME/Project nodeType:Role scope:selfAndAncestors");
    }

    [Fact]
    public void Attribute_ResolveQueries_ResolvesMultipleQueries()
    {
        var queries = new[]
        {
            "namespace:{node.namespace} nodeType:Role",
            "path:{node.path} nodeType:Group"
        };

        var resolved = MeshNodeCollectionAttribute.ResolveQueries(queries, "Org/Team", "Org/Team/Item");

        resolved.Should().HaveCount(2);
        resolved[0].Should().Be("namespace:Org/Team nodeType:Role");
        resolved[1].Should().Be("path:Org/Team/Item nodeType:Group");
    }

    [Fact]
    public void Attribute_ResolveQueries_EmptyQueries_ReturnsEmpty()
    {
        var resolved = MeshNodeCollectionAttribute.ResolveQueries([], "ACME");

        resolved.Should().BeEmpty();
    }
}
