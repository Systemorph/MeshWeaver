using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="NodeTypeConfigForm"/> — the view model driving the
/// inline settings editor in the NodeType Configuration pane. Covers round-tripping
/// of Name/Icon from the <see cref="MeshNode"/> itself plus Description/ChildrenQuery/
/// DefaultNamespace/PageMaxWidth from the <see cref="NodeTypeDefinition"/> content.
/// </summary>
public class NodeTypeConfigFormTest
{
    [Fact]
    public void FromNode_ReadsAllEditableFields()
    {
        var node = new MeshNode("Project", "Acme")
        {
            Name = "Project Display Name",
            Icon = "content:icon.svg",
            NodeType = MeshNode.NodeTypePath
        };
        var def = new NodeTypeDefinition
        {
            Description = "Long-form description",
            ChildrenQuery = "nodeType:Story scope:descendants",
            DefaultNamespace = "Acme",
            PageMaxWidth = "960px",
            Configuration = "config => config"
        };

        var form = NodeTypeConfigForm.FromNode(node, def);

        form.Name.Should().Be("Project Display Name");
        form.Icon.Should().Be("content:icon.svg");
        form.Description.Should().Be("Long-form description");
        form.ChildrenQuery.Should().Be("nodeType:Story scope:descendants");
        form.DefaultNamespace.Should().Be("Acme");
        form.PageMaxWidth.Should().Be("960px");
    }

    [Fact]
    public void FromNode_WithNullDefinition_IgnoresDefinitionFields()
    {
        var node = new MeshNode("Project", "Acme")
        {
            Name = "Only a name",
            Icon = null
        };

        var form = NodeTypeConfigForm.FromNode(node, def: null);

        form.Name.Should().Be("Only a name");
        form.Icon.Should().BeNull();
        form.Description.Should().BeNull();
        form.ChildrenQuery.Should().BeNull();
        form.DefaultNamespace.Should().BeNull();
        form.PageMaxWidth.Should().BeNull();
    }

    [Fact]
    public void FromNode_WithDefinitionButNoOptionalFields_ReturnsNulls()
    {
        var node = new MeshNode("Project");
        var def = new NodeTypeDefinition { Configuration = "config => config" };

        var form = NodeTypeConfigForm.FromNode(node, def);

        form.Description.Should().BeNull();
        form.ChildrenQuery.Should().BeNull();
        form.DefaultNamespace.Should().BeNull();
        form.PageMaxWidth.Should().BeNull();
    }
}
