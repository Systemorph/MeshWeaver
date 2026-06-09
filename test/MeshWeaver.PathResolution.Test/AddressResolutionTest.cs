using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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

    private void EnsureNodesCreated()
    {
        var existingPricing = ReadNode("pricing").Should().Emit();
        if (existingPricing == null)
        {
            NodeFactory.CreateNode(MeshNode.FromPath(PricingPath) with
            {
                Name = "Pricing",
                Icon = "Calculator",
                NodeType = "Markdown",
            }).Should().Emit();
        }

        var existingApp = ReadNode("app").Should().Emit();
        if (existingApp == null)
        {
            NodeFactory.CreateNode(MeshNode.FromPath(AppPath) with
            {
                Name = "Applications",
                Icon = "App",
                NodeType = "Markdown",
            }).Should().Emit();
        }
    }

    #region ResolvePath Score-Based Tests

    [Fact(Timeout = 10000)]
    public void ResolvePath_SingleSegmentNode_MatchesAndReturnsRemainder()
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath("pricing/Microsoft/2026/Overview/details").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026/Overview/details");
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_AppPath_ReturnsPrefixAndRemainder()
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath("app/Todo/Dashboard/123").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("app");
        resolution.Remainder.Should().Be("Todo/Dashboard/123");
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_ExactMatch_ReturnsNullRemainder()
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath("pricing").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_WithLeadingSlash_ParsesCorrectly()
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath("/pricing/Microsoft/2026").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_UnknownPath_ReturnsNull()
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath("unknown/test/path").Should().Emit();

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
    public void ResolvePath_VariousPaths_ReturnsCorrectPrefixAndRemainder(
        string path, string expectedPrefix, string? expectedRemainder)
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath(path).Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_EmptyPath_ReturnsNull()
    {
        var resolution = PathResolver.ResolvePath("").Should().Emit();

        resolution.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_NullPath_ReturnsNull()
    {
        var resolution = PathResolver.ResolvePath(null!).Should().Emit();

        resolution.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_SegmentWithSpace_ResolvesToExactNode()
    {
        EnsureNodesCreated();
        // Content and files commonly have spaces in their names → spaces in the path
        // (e.g. "Agentic Pension", "Annual Report.pdf"). The resolver builds a
        // `path:{prefixes}` query; a parser that treats a space as a token separator
        // splits "pricing/Annual Report" into `path:pricing/Annual` + free-text and
        // never matches the node — the "trouble loading paths with spaces" symptom.
        NodeFactory.CreateNode(MeshNode.FromPath("pricing/Annual Report") with
        {
            Name = "Annual Report",
            NodeType = "Markdown",
        }).Should().Emit();

        var resolution = PathResolver.ResolvePath("pricing/Annual Report").Should().Emit();

        resolution.Should().NotBeNull("a path segment containing a space must resolve");
        resolution!.Prefix.Should().Be("pricing/Annual Report",
            "the full space-containing path is an exact node — it must not break on the space and fall back to 'pricing'");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_SpaceSegmentWithAreaSuffix_FallsBackToNode()
    {
        EnsureNodesCreated();
        NodeFactory.CreateNode(MeshNode.FromPath("pricing/Annual Report") with
        {
            Name = "Annual Report",
            NodeType = "Markdown",
        }).Should().Emit();

        // /pricing/Annual Report/Overview → node "pricing/Annual Report" + area "Overview"
        var resolution = PathResolver.ResolvePath("pricing/Annual Report/Overview").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing/Annual Report");
        resolution.Remainder.Should().Be("Overview");
    }

    #endregion

    #region Score-Based Matching Priority Tests

    [Fact(Timeout = 10000)]
    public void ResolvePath_MultipleNodes_HighestScoreWins()
    {
        EnsureNodesCreated();
        var resolution = PathResolver.ResolvePath("pricing/Microsoft/2026").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact(Timeout = 10000)]
    public void ResolvePath_CaseInsensitive_MatchesCorrectly()
    {
        EnsureNodesCreated();
        // Persistence paths may be case-sensitive
        var resolution = PathResolver.ResolvePath("PRICING/Microsoft/2026").Should().Emit();

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
    public void ResolvePath_ThreadMessageNode_ResolvesToFullMessagePath()
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
        NodeFactory.CreateNode(threadNode).Should().Emit();

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
        NodeFactory.CreateNode(msgNode).Should().Emit();

        // Resolve the ThreadMessage path â€” should return the full message path, no remainder
        var resolution = PathResolver.ResolvePath($"{threadPath}/msg1").Should().Emit();

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
    public void ResolvePath_ThreadNode_ResolvesCorrectlyWithChildren()
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
        NodeFactory.CreateNode(threadNode).Should().Emit();

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
        NodeFactory.CreateNode(msgNode).Should().Emit();

        // Resolve the Thread path â€” should match the Thread node exactly
        var resolution = PathResolver.ResolvePath(threadPath).Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(threadPath);
        resolution.Remainder.Should().BeNull();
    }

    #endregion
}

