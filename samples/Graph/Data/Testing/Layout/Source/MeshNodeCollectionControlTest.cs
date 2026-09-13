// <meshweaver>
// Id: Testing/Layout/MeshNodeCollectionControlTest
// DisplayName: Testing/Layout/MeshNodeCollectionControlTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Domain;

public class MeshNodeCollectionControlTest
{
    [MeshFact]
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

    [MeshFact]
    public void WithQueries_SetsMultipleQueries()
    {
        var control = new MeshNodeCollectionControl()
            .WithQueries("namespace:X nodeType:AccessAssignment", "path:Y nodeType:Group");

        control.Queries.Should().HaveCount(2);
        control.Queries[0].Should().Be("namespace:X nodeType:AccessAssignment");
        control.Queries[1].Should().Be("path:Y nodeType:Group");
    }

    [MeshFact]
    public void WithDeletable_SetsFlag()
    {
        var control = new MeshNodeCollectionControl()
            .WithDeletable(true);

        control.Deletable.Should().BeTrue();
    }

    [MeshFact]
    public void WithShowAdd_SetsFlag()
    {
        var control = new MeshNodeCollectionControl()
            .WithShowAdd(false);

        control.ShowAdd.Should().BeFalse();
    }

    [MeshFact]
    public void WithAddDialogTitle_SetsTitle()
    {
        var control = new MeshNodeCollectionControl()
            .WithAddDialogTitle("Add Assignment");

        control.AddDialogTitle.Should().Be("Add Assignment");
    }

    [MeshFact]
    public void WithAddPickerQueries_SetsMultipleQueries()
    {
        var control = new MeshNodeCollectionControl()
            .WithAddPickerQueries("namespace:User nodeType:User", "namespace:X nodeType:Group");

        control.AddPickerQueries.Should().HaveCount(2);
        control.AddPickerQueries![0].Should().Be("namespace:User nodeType:User");
        control.AddPickerQueries[1].Should().Be("namespace:X nodeType:Group");
    }

    [MeshFact]
    public void WithAddPickerLabel_SetsLabel()
    {
        var control = new MeshNodeCollectionControl()
            .WithAddPickerLabel("Subject (User or Group)");

        control.AddPickerLabel.Should().Be("Subject (User or Group)");
    }

    [MeshFact]
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

    [MeshFact]
    public void Attribute_StoresQueries()
    {
        var attr = new MeshNodeCollectionAttribute(
            "namespace:{node.namespace} nodeType:Role scope:selfAndAncestors"
        );

        attr.Queries.Should().HaveCount(1);
        attr.Queries[0].Should().Be("namespace:{node.namespace} nodeType:Role scope:selfAndAncestors");
    }

    [MeshFact]
    public void Attribute_MultipleQueries()
    {
        var attr = new MeshNodeCollectionAttribute(
            "namespace:User nodeType:User",
            "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors"
        );

        attr.Queries.Should().HaveCount(2);
    }

    [MeshFact]
    public void Attribute_ResolveQueries_ResolvesTemplateVariables()
    {
        var queries = new[] { "namespace:{node.namespace} nodeType:Role scope:selfAndAncestors" };

        var resolved = MeshNodeCollectionAttribute.ResolveQueries(queries, "ACME/Project");

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("namespace:ACME/Project nodeType:Role scope:selfAndAncestors");
    }

    [MeshFact]
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

    [MeshFact]
    public void Attribute_ResolveQueries_EmptyQueries_ReturnsEmpty()
    {
        var resolved = MeshNodeCollectionAttribute.ResolveQueries([], "ACME");

        resolved.Should().BeEmpty();
    }

    [MeshFact]
    public void CanAdd_UnboundedByDefault()
    {
        var control = new MeshNodeCollectionControl();

        control.Max.Should().Be(int.MaxValue);
        control.Min.Should().Be(0);
        control.CanAdd(0).Should().BeTrue();
        control.CanAdd(10_000).Should().BeTrue();
    }

    [MeshFact]
    public void CanAdd_StopsAtMax()
    {
        var control = new MeshNodeCollectionControl().WithMax(3);

        control.CanAdd(2).Should().BeTrue();
        control.CanAdd(3).Should().BeFalse("the maximum is reached");
        control.CanAdd(4).Should().BeFalse();
    }

    [MeshFact]
    public void CanAdd_RespectsShowAdd()
    {
        new MeshNodeCollectionControl().WithShowAdd(false).CanAdd(0).Should().BeFalse();
    }

    [MeshFact]
    public void CanDelete_KeepsTheMinimum()
    {
        var control = new MeshNodeCollectionControl().WithDeletable(true).WithMin(1);

        control.CanDelete(2).Should().BeTrue();
        control.CanDelete(1).Should().BeFalse("deleting would breach the minimum");
        control.CanDelete(0).Should().BeFalse();
    }

    [MeshFact]
    public void CanDelete_RequiresDeletable()
    {
        new MeshNodeCollectionControl().WithMin(0).CanDelete(5).Should().BeFalse("deletable is off by default");
        new MeshNodeCollectionControl().WithDeletable(true).CanDelete(5).Should().BeTrue();
    }
}
