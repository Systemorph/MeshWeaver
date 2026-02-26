using FluentAssertions;
using MeshWeaver.Domain;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class MeshNodeAttributeResolveQueriesTest
{
    [Fact]
    public void ResolveQueries_ReplacesNodeNamespace()
    {
        var queries = new[] { "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, "ACME/Software/Project");

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("namespace:ACME/Software/Project nodeType:Group scope:selfAndAncestors");
    }

    [Fact]
    public void ResolveQueries_ReplacesNodePath()
    {
        var queries = new[] { "path:{node.path} nodeType:AccessAssignment" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, null, "ACME/Software/Project/Alice_Access");

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("path:ACME/Software/Project/Alice_Access nodeType:AccessAssignment");
    }

    [Fact]
    public void ResolveQueries_ReplacesBothVariables()
    {
        var queries = new[] { "namespace:{node.namespace} path:{node.path} nodeType:Role" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, "ACME", "ACME/Software/Alice_Access");

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("namespace:ACME path:ACME/Software/Alice_Access nodeType:Role");
    }

    [Fact]
    public void ResolveQueries_MultipleQueries_ResolvesAll()
    {
        var queries = new[]
        {
            "namespace:User nodeType:User",
            "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors"
        };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, "Org/Team");

        resolved.Should().HaveCount(2);
        resolved[0].Should().Be("namespace:User nodeType:User", "first query has no variables to resolve");
        resolved[1].Should().Be("namespace:Org/Team nodeType:Group scope:selfAndAncestors");
    }

    [Fact]
    public void ResolveQueries_NullNamespace_LeavesPlaceholder()
    {
        var queries = new[] { "namespace:{node.namespace} nodeType:Role scope:selfAndAncestors" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, null);

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("namespace:{node.namespace} nodeType:Role scope:selfAndAncestors",
            "unresolved variables should remain as-is when value is null");
    }

    [Fact]
    public void ResolveQueries_EmptyNamespace_ReplacesWithEmpty()
    {
        var queries = new[] { "namespace:{node.namespace} nodeType:Role" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, "");

        resolved.Should().HaveCount(1);
        resolved[0].Should().Be("namespace: nodeType:Role");
    }

    [Fact]
    public void ResolveQueries_EmptyQueries_ReturnsEmpty()
    {
        var resolved = MeshNodeAttribute.ResolveQueries([], "ACME");

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void ResolveQueries_NullQueries_ReturnsEmpty()
    {
        var resolved = MeshNodeAttribute.ResolveQueries(null!, "ACME");

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void ResolveQueries_NoVariables_ReturnsUnchanged()
    {
        var queries = new[] { "namespace:User nodeType:User", "nodeType:Role" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, "ACME", "ACME/Software/Alice_Access");

        resolved.Should().BeEquivalentTo(queries);
    }

    [Fact]
    public void ResolveQueries_DeepNamespace_ResolvesCorrectly()
    {
        var queries = new[] { "namespace:{node.namespace} nodeType:Role scope:selfAndAncestors" };

        var resolved = MeshNodeAttribute.ResolveQueries(queries, "Org/Team/SubTeam/Project");

        resolved[0].Should().Be("namespace:Org/Team/SubTeam/Project nodeType:Role scope:selfAndAncestors");
    }

    [Fact]
    public void Attribute_QueriesProperty_StoresAllValues()
    {
        var attr = new MeshNodeAttribute(
            "namespace:User nodeType:User",
            "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors"
        );

        attr.Queries.Should().HaveCount(2);
        attr.Queries[0].Should().Be("namespace:User nodeType:User");
        attr.Queries[1].Should().Be("namespace:{node.namespace} nodeType:Group scope:selfAndAncestors");
    }
}
