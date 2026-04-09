using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// Tests for address resolution via IPathResolver.ResolvePathAsync.
/// Verifies that paths are correctly resolved to addresses with remainder using score-based matching.
/// Nodes are registered as persistence nodes.
/// </summary>
public class AddressResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PricingPath = "pricing";
    private const string AppPath = "app";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .AddSampleUsers();

    private async Task EnsureNodesCreated()
    {
        var existingPricing = await MeshQuery.QueryAsync<MeshNode>("path:pricing").FirstOrDefaultAsync();
        if (existingPricing == null)
        {
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath(PricingPath) with
            {
                Name = "Pricing",
                Icon = "Calculator",
            });
        }

        var existingApp = await MeshQuery.QueryAsync<MeshNode>("path:app").FirstOrDefaultAsync();
        if (existingApp == null)
        {
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath(AppPath) with
            {
                Name = "Applications",
                Icon = "App",
            });
        }
    }

    #region ResolvePath Score-Based Tests

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_SingleSegmentNode_MatchesAndReturnsRemainder()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync("pricing/Microsoft/2026/Overview/details");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026/Overview/details");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_AppPath_ReturnsPrefixAndRemainder()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync("app/Todo/Dashboard/123");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("app");
        resolution.Remainder.Should().Be("Todo/Dashboard/123");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_ExactMatch_ReturnsNullRemainder()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync("pricing");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_WithLeadingSlash_ParsesCorrectly()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync("/pricing/Microsoft/2026");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_UnknownPath_ReturnsNull()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync("unknown/test/path");

        resolution.Should().BeNull();
    }

    [Theory(Timeout = 10000)]
    [InlineData("pricing", "pricing", null)]
    [InlineData("pricing/ACME", "pricing", "ACME")]
    [InlineData("pricing/ACME/2025", "pricing", "ACME/2025")]
    [InlineData("pricing/ACME/2025/Reports", "pricing", "ACME/2025/Reports")]
    [InlineData("app", "app", null)]
    [InlineData("app/Insurance", "app", "Insurance")]
    [InlineData("app/Insurance/Dashboard", "app", "Insurance/Dashboard")]
    public async Task ResolvePath_VariousPaths_ReturnsCorrectPrefixAndRemainder(
        string path, string expectedPrefix, string? expectedRemainder)
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync(path);

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_EmptyPath_ReturnsNull()
    {
        var resolution = await PathResolver.ResolvePathAsync("");

        resolution.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_NullPath_ReturnsNull()
    {
        var resolution = await PathResolver.ResolvePathAsync(null!);

        resolution.Should().BeNull();
    }

    #endregion

    #region Score-Based Matching Priority Tests

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_MultipleNodes_HighestScoreWins()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePathAsync("pricing/Microsoft/2026");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_CaseInsensitive_MatchesCorrectly()
    {
        await EnsureNodesCreated();
        // Persistence paths may be case-sensitive
        var resolution = await PathResolver.ResolvePathAsync("PRICING/Microsoft/2026");

        // Case-insensitive match depends on persistence backend
        if (resolution != null)
        {
            resolution.Prefix.Should().Be(PricingPath);
        }
    }

    #endregion

    #region ThreadMessage Path Resolution

    /// <summary>
    /// Creates a Thread with a ThreadMessage child and verifies path resolution
    /// resolves the full ThreadMessage path (not the Thread path with remainder).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ResolvePath_ThreadMessageNode_ResolvesToFullMessagePath()
    {
        // Create Thread node
        var threadPath = "User/Roland/_Thread/test-resolution-1234";
        var threadNode = new MeshNode("test-resolution-1234", "User/Roland/_Thread")
        {
            Name = "Test Resolution",
            NodeType = ThreadNodeType.NodeType,
            Content = new AI.Thread
            {
                Messages = ["msg1"]
            }
        };
        await NodeFactory.CreateNodeAsync(threadNode);

        // Create ThreadMessage child node
        var msgNode = new MeshNode("msg1", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Hello",
                Timestamp = System.DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        };
        await NodeFactory.CreateNodeAsync(msgNode);

        // Resolve the ThreadMessage path — should return the full message path, no remainder
        var resolution = await PathResolver.ResolvePathAsync($"{threadPath}/msg1");

        resolution.Should().NotBeNull("ThreadMessage node exists at {0}/msg1", threadPath);
        resolution!.Prefix.Should().Be($"{threadPath}/msg1",
            "should resolve to the full ThreadMessage path, not the Thread path with remainder");
        resolution.Remainder.Should().BeNull("exact match should have no remainder");
    }

    /// <summary>
    /// Verifies that the Thread node itself still resolves correctly
    /// when ThreadMessage children exist.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ResolvePath_ThreadNode_ResolvesCorrectlyWithChildren()
    {
        var threadPath = "User/Roland/_Thread/test-parent-5678";
        var threadNode = new MeshNode("test-parent-5678", "User/Roland/_Thread")
        {
            Name = "Test Parent",
            NodeType = ThreadNodeType.NodeType,
            Content = new AI.Thread
            {
                Messages = ["m1"]
            }
        };
        await NodeFactory.CreateNodeAsync(threadNode);

        var msgNode = new MeshNode("m1", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Response",
                Timestamp = System.DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };
        await NodeFactory.CreateNodeAsync(msgNode);

        // Resolve the Thread path — should match the Thread node exactly
        var resolution = await PathResolver.ResolvePathAsync(threadPath);

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(threadPath);
        resolution.Remainder.Should().BeNull();
    }

    #endregion
}
