using System.Collections.Generic;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests for the query routing hints pipeline.
/// Verifies that routing rules correctly resolve partition and table hints from ParsedQuery.
/// </summary>
public class QueryRoutingHintsTest
{
    private readonly QueryParser _parser = new();

    [Fact]
    public void PathRule_ResolvesPartitionFromPath()
    {
        // Rule: first path segment → partition
        QueryRoutingRule rule = query =>
        {
            var path = query.Path;
            if (string.IsNullOrEmpty(path)) return null;
            var slash = path.IndexOf('/');
            var firstSeg = slash > 0 ? path[..slash] : path;
            return new QueryRoutingHints { Partition = firstSeg };
        };

        var parsed = _parser.Parse("namespace:User/alice nodeType:Thread");
        var hint = rule(parsed);

        hint.Should().NotBeNull();
        hint!.Partition.Should().Be("User");
        hint.Table.Should().BeNull();
    }

    [Fact]
    public void PathRule_ReturnsNullForNoPath()
    {
        QueryRoutingRule rule = query =>
        {
            var path = query.Path;
            if (string.IsNullOrEmpty(path)) return null;
            return new QueryRoutingHints { Partition = path };
        };

        var parsed = _parser.Parse("nodeType:User");
        var hint = rule(parsed);

        hint.Should().BeNull();
    }

    [Fact]
    public void NodeTypeRule_ResolvesPartitionForUser()
    {
        QueryRoutingRule rule = query =>
            query.ExtractNodeType() == "User" ? new QueryRoutingHints { Partition = "User" } : null;

        var parsed = _parser.Parse("nodeType:User");
        var hint = rule(parsed);

        hint.Should().NotBeNull();
        hint!.Partition.Should().Be("User");
    }

    [Fact]
    public void NodeTypeRule_ReturnsNullForOtherTypes()
    {
        QueryRoutingRule rule = query =>
            query.ExtractNodeType() == "User" ? new QueryRoutingHints { Partition = "User" } : null;

        var parsed = _parser.Parse("nodeType:Thread");
        var hint = rule(parsed);

        hint.Should().BeNull();
    }

    [Fact]
    public void TableRule_ResolvesAccessAssignmentTable()
    {
        QueryRoutingRule rule = query =>
        {
            var nt = query.ExtractNodeType();
            return nt switch
            {
                "AccessAssignment" => new QueryRoutingHints { Table = "access" },
                "Thread" => new QueryRoutingHints { Table = "threads" },
                _ => null
            };
        };

        var parsed = _parser.Parse("nodeType:AccessAssignment");
        var hint = rule(parsed);

        hint.Should().NotBeNull();
        hint!.Table.Should().Be("access");
        hint.Partition.Should().BeNull();
    }

    [Fact]
    public void TableRule_ResolvesThreadTable()
    {
        QueryRoutingRule rule = query =>
        {
            var nt = query.ExtractNodeType();
            return nt switch
            {
                "Thread" => new QueryRoutingHints { Table = "threads" },
                _ => null
            };
        };

        var parsed = _parser.Parse("nodeType:Thread");
        var hint = rule(parsed);

        hint.Should().NotBeNull();
        hint!.Table.Should().Be("threads");
    }

    [Fact]
    public void MeshConfiguration_ResolveRoutingHints_FirstNonNullWins()
    {
        var rules = new List<QueryRoutingRule>
        {
            // Rule 1: returns null (abstains)
            _ => null,
            // Rule 2: returns partition
            query => query.ExtractNodeType() == "User"
                ? new QueryRoutingHints { Partition = "User" }
                : null,
            // Rule 3: would also return partition, but Rule 2 wins
            _ => new QueryRoutingHints { Partition = "ShouldNotWin" }
        };

        var config = new MeshConfiguration(
            new Dictionary<string, MeshNode>(),
            queryRoutingRules: rules);

        var parsed = _parser.Parse("nodeType:User");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().Be("User");
    }

    [Fact]
    public void MeshConfiguration_ResolveRoutingHints_CombinesPartitionAndTable()
    {
        var rules = new List<QueryRoutingRule>
        {
            // Rule 1: resolves partition from path
            query =>
            {
                var path = query.Path;
                if (string.IsNullOrEmpty(path)) return null;
                var slash = path.IndexOf('/');
                var firstSeg = slash > 0 ? path[..slash] : path;
                return new QueryRoutingHints { Partition = firstSeg };
            },
            // Rule 2: resolves table from nodeType
            query => query.ExtractNodeType() switch
            {
                "AccessAssignment" => new QueryRoutingHints { Table = "access" },
                _ => null
            }
        };

        var config = new MeshConfiguration(
            new Dictionary<string, MeshNode>(),
            queryRoutingRules: rules);

        var parsed = _parser.Parse("namespace:Admin/_Access nodeType:AccessAssignment");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().Be("Admin");
        hints.Table.Should().Be("access");
    }

    [Fact]
    public void MeshConfiguration_ResolveRoutingHints_NoRulesMatch_ReturnsNulls()
    {
        var rules = new List<QueryRoutingRule>
        {
            query => query.ExtractNodeType() == "User"
                ? new QueryRoutingHints { Partition = "User" }
                : null
        };

        var config = new MeshConfiguration(
            new Dictionary<string, MeshNode>(),
            queryRoutingRules: rules);

        var parsed = _parser.Parse("laptop"); // text search, no nodeType
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().BeNull();
        hints.Table.Should().BeNull();
    }

    [Fact]
    public void MeshConfiguration_ResolveRoutingHints_EmptyRules()
    {
        var config = new MeshConfiguration(new Dictionary<string, MeshNode>());

        var parsed = _parser.Parse("nodeType:User");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().BeNull();
        hints.Table.Should().BeNull();
    }
}
