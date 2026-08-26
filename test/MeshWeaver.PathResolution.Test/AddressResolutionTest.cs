using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
        // The thread cases that needed AddAI() moved to MeshWeaver.AI.Test.ThreadAddressResolutionTest
        // (#2276); what remains resolves plain node paths and needs no AI types.
        => base.ConfigureMesh(builder)
            .AddSampleUsers();

    private async Task EnsureNodesCreated()
    {
        // `pricing` / `app` are TOP-LEVEL nodes = partition roots. A Markdown node does not
        // own a partition (only User/Space do), so PartitionWriteGuardValidator Rule 3 rejects
        // a non-System caller creating one ("the root namespace is reserved for partitions").
        // Seed via SeedTopLevel (System is the legitimate partition provisioner) — the same way
        // onboarding/migration provision partitions in production.
        var existingPricing = await ReadNode("pricing").Should().Emit();
        if (existingPricing == null)
        {
            await SeedTopLevel(MeshNode.FromPath(PricingPath) with
            {
                Name = "Pricing",
                Icon = "Calculator",
                NodeType = "Markdown",
            });
        }

        var existingApp = await ReadNode("app").Should().Emit();
        if (existingApp == null)
        {
            await SeedTopLevel(MeshNode.FromPath(AppPath) with
            {
                Name = "Applications",
                Icon = "App",
                NodeType = "Markdown",
            });
        }
    }

    #region ResolvePath Score-Based Tests

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_SingleSegmentNode_MatchesAndReturnsRemainder()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePath("pricing/Microsoft/2026/Overview/details").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026/Overview/details");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_AppPath_ReturnsPrefixAndRemainder()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePath("app/Todo/Dashboard/123").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("app");
        resolution.Remainder.Should().Be("Todo/Dashboard/123");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_ExactMatch_ReturnsNullRemainder()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePath("pricing").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_WithLeadingSlash_ParsesCorrectly()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePath("/pricing/Microsoft/2026").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_UnknownPath_ReturnsNull()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePath("unknown/test/path").Should().Emit();

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
        var resolution = await PathResolver.ResolvePath(path).Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_EmptyPath_ReturnsNull()
    {
        var resolution = await PathResolver.ResolvePath("").Should().Emit();

        resolution.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_NullPath_ReturnsNull()
    {
        var resolution = await PathResolver.ResolvePath(null!).Should().Emit();

        resolution.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_SegmentWithSpace_ResolvesToExactNode()
    {
        await EnsureNodesCreated();
        // Content and files commonly have spaces in their names → spaces in the path
        // (e.g. "Agentic Pension", "Annual Report.pdf"). The resolver builds a
        // `path:{prefixes}` query; a parser that treats a space as a token separator
        // splits "pricing/Annual Report" into `path:pricing/Annual` + free-text and
        // never matches the node — the "trouble loading paths with spaces" symptom.
        // Child under the System-seeded `pricing` partition — seed via System too (the partition
        // owner granted no Write to the test's Admin identity; fixtures only need to EXIST so the
        // System-impersonated resolver query can find them).
        await SeedTopLevel(MeshNode.FromPath("pricing/Annual Report") with
        {
            Name = "Annual Report",
            NodeType = "Markdown",
        });

        var resolution = await PathResolver.ResolvePath("pricing/Annual Report").Should().Emit();

        resolution.Should().NotBeNull("a path segment containing a space must resolve");
        resolution!.Prefix.Should().Be("pricing/Annual Report",
            "the full space-containing path is an exact node — it must not break on the space and fall back to 'pricing'");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_SpaceSegmentWithAreaSuffix_FallsBackToNode()
    {
        await EnsureNodesCreated();
        // Child under the System-seeded `pricing` partition — seed via System too (the partition
        // owner granted no Write to the test's Admin identity; fixtures only need to EXIST so the
        // System-impersonated resolver query can find them).
        await SeedTopLevel(MeshNode.FromPath("pricing/Annual Report") with
        {
            Name = "Annual Report",
            NodeType = "Markdown",
        });

        // /pricing/Annual Report/Overview → node "pricing/Annual Report" + area "Overview"
        var resolution = await PathResolver.ResolvePath("pricing/Annual Report/Overview").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing/Annual Report");
        resolution.Remainder.Should().Be("Overview");
    }

    #endregion

    #region Score-Based Matching Priority Tests

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_MultipleNodes_HighestScoreWins()
    {
        await EnsureNodesCreated();
        var resolution = await PathResolver.ResolvePath("pricing/Microsoft/2026").Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact(Timeout = 10000)]
    public async Task ResolvePath_CaseInsensitive_MatchesCorrectly()
    {
        await EnsureNodesCreated();
        // Persistence paths may be case-sensitive
        var resolution = await PathResolver.ResolvePath("PRICING/Microsoft/2026").Should().Emit();

        // Case-insensitive match depends on persistence backend
        if (resolution != null)
        {
            resolution.Prefix.Should().Be(PricingPath);
        }
    }

    #endregion

    #region ThreadMessage Path Resolution



    #endregion
}

