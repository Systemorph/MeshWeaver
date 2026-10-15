using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 An exact-name search for a person who exists must find them, however the agent spells the
/// location (#6828). Measured on a portal: <c>name:"First Last" basePath:@space</c> answered
/// <c>count: 0</c> for a contact two levels below the space and for a node in the user's own home,
/// so the assistant reported the person as unfindable. The name half was never the problem —
/// <c>name:</c> is a case-insensitive exact match on <c>MeshNode.Name</c>, quoted multi-word
/// values included, on both executors. The LOCATION half was: <c>@space</c> written into the query
/// text kept its <c>@</c> and anchored at a partition literally called <c>@space</c>, and
/// <c>basePath:</c> was not a qualifier but a filter on a property no node has.
///
/// <para>The control on every positive: the same location with a name that does not exist answers
/// zero, so a passing positive is the name filter AND the location working, not a filter dropped.</para>
/// </summary>
public class SearchResolvesInQueryLocationsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "NameSearchSpace";
    private const string Person = "Jane Q Example";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // The contact sits two levels below the space root, as a contact under an opportunity does.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode(Space) { Name = "Name search space", NodeType = "Markdown" },
            new MeshNode("Deal", Space) { Name = "A deal", NodeType = "Markdown" },
            new MeshNode("Jane", $"{Space}/Deal") { Name = Person, NodeType = "Markdown" });

    private Task<string> Search(
        string query, string? basePath = null, CancellationToken cancellationToken = default)
        => new MeshOperations(Mesh).Search(query, basePath, 50)
            .FirstAsync().Timeout(Budget).Await(cancellationToken);

    public static TheoryData<string, string?> Shapes => new()
    {
        // The measured shape: basePath written into the query text AND passed as the argument.
        { $"name:\"{{0}}\" basePath:@{Space}", $"@{Space}" },
        // basePath written only into the query text.
        { $"name:\"{{0}}\" basePath:@{Space}", null },
        // The reference prefix on an ordinary namespace anchor.
        { $"namespace:@{Space} scope:descendants name:\"{{0}}\"", null },
        { $"path:@/{Space} scope:descendants name:\"{{0}}\"", null },
        // The argument alone — the shape that always worked, kept as the baseline.
        { $"name:\"{{0}}\"", $"@{Space}" },
    };

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Shapes))]
    public async Task AnExactNameIsFound_WhereverTheLocationIsWritten(string shape, string? basePath)
    {
        var ct = TestContext.Current.CancellationToken;

        var found = await Search(string.Format(shape, Person), basePath, ct);
        Output.WriteLine(found);
        found.Should().NotStartWith("Error:", "the query names its location");
        var envelope = JsonDocument.Parse(found).RootElement;
        envelope.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("path").GetString())
            .Should().Equal(new[] { $"{Space}/Deal/Jane" }, "the person exists two levels below the space");
        envelope.GetProperty("coverage").GetProperty("partitions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { Space }, "the read ran against the space the query named");

        // Case-insensitive, as both executors compare names.
        var lower = await Search(string.Format(shape, Person.ToLowerInvariant()), basePath, ct);
        JsonDocument.Parse(lower).RootElement.GetProperty("count").GetInt32().Should().Be(1);

        // Control: the same location with a name nobody has answers zero, so the positive above
        // is the name filter working, not a filter that was dropped.
        var absent = await Search(string.Format(shape, "Nobody Of That Name"), basePath, ct);
        Output.WriteLine(absent);
        JsonDocument.Parse(absent).RootElement.GetProperty("count").GetInt32().Should().Be(0);
    }
}
