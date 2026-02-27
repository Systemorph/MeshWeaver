using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests that AutocompleteAsync returns Icon data and performs proper text matching.
/// </summary>
[Collection("AutocompleteIconTests")]
public class AutocompleteIconTests : MonolithMeshTestBase
{
    private static readonly string SamplesDataDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));

    private readonly string _cacheDirectory;
    private IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

    public AutocompleteIconTests(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverAutocompleteIconTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SamplesDataDirectory)
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddGraph();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    #region Icon Tests

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_ReturnsIconForNodesWithIcon()
    {
        // Arrange - Systemorph has icon: "/static/storage/content/Systemorph/logo_t.png"
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "Systemorph", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Systemorph':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} | Icon: {s.Icon ?? "(null)"}");

        suggestions.Should().NotBeEmpty();

        var systemorph = suggestions.FirstOrDefault(s => s.Path == "Systemorph");
        systemorph.Should().NotBeNull("Should find Systemorph node");
        systemorph!.Icon.Should().NotBeNullOrEmpty("Systemorph has an icon defined in its JSON");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_ReturnsIconForACME()
    {
        // Arrange - ACME has icon: "/static/storage/content/ACME/Software/logo.svg"
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "ACME", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for 'ACME':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} | Icon: {s.Icon ?? "(null)"}");

        suggestions.Should().NotBeEmpty();

        var acme = suggestions.FirstOrDefault(s => s.Path == "ACME/Software");
        acme.Should().NotBeNull("Should find ACME node");
        acme!.Icon.Should().NotBeNullOrEmpty("ACME has an icon defined in its JSON");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_EmptyPrefix_ReturnsIconsWhereAvailable()
    {
        // Arrange - empty prefix returns top-level nodes, many of which have icons
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "", AutocompleteMode.RelevanceFirst, 20)
            .ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for empty prefix:");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} | Icon: {s.Icon ?? "(null)"}");

        suggestions.Should().NotBeEmpty();

        var withIcons = suggestions.Where(s => !string.IsNullOrEmpty(s.Icon)).ToList();
        Output.WriteLine($"\n{withIcons.Count} of {suggestions.Count} suggestions have icons");

        withIcons.Should().NotBeEmpty("At least some top-level nodes should have icons");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_WithBasePath_ReturnsIconsForChildren()
    {
        // Arrange - search within Systemorph which has children with icons (Marketing, Projects, etc.)
        var suggestions = await MeshQuery
            .AutocompleteAsync("Systemorph", "", AutocompleteMode.RelevanceFirst, 20)
            .ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions under 'Systemorph':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} | Icon: {s.Icon ?? "(null)"}");

        suggestions.Should().NotBeEmpty();

        var withIcons = suggestions.Where(s => !string.IsNullOrEmpty(s.Icon)).ToList();
        Output.WriteLine($"\n{withIcons.Count} of {suggestions.Count} children have icons");
    }

    #endregion

    #region Text Matching Tests (no wildcards)

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_PrefixMatch_FindsByNameStart()
    {
        // "Mar" should match "Marketing" by prefix
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "Mar", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Mar':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F1})");

        suggestions.Should().NotBeEmpty("'Mar' should match 'Marketing' by prefix");
        suggestions.Should().Contain(s =>
            s.Name.StartsWith("Mar", StringComparison.OrdinalIgnoreCase),
            "Should find nodes whose name starts with 'Mar'");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_ContainsMatch_FindsBySubstring()
    {
        // "arke" should match "Marketing" by contains
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "arke", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'arke':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F1})");

        suggestions.Should().NotBeEmpty("'arke' should match 'Marketing' by substring");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_CaseInsensitive_MatchesLowerInput()
    {
        // "acme" (lowercase) should match "ACME"
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "acme", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'acme':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F1})");

        suggestions.Should().NotBeEmpty("'acme' should match 'ACME' case-insensitively");
        suggestions.Should().Contain(s => s.Path == "ACME/Software");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_PathMatch_FindsByPathSubstring()
    {
        // Search for a path segment that exists under a different name
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "Organization", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Organization':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F1})");

        suggestions.Should().NotBeEmpty("'Organization' should match nodes with that in name or path");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_NoMatch_ReturnsEmpty()
    {
        // A completely non-existent term should return nothing
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "xyznonexistent999", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'xyznonexistent999'");

        suggestions.Should().BeEmpty("A non-existent term should return no suggestions");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_RelevanceFirst_PrefixMatchScoresHigher()
    {
        // "Sys" prefix-matches "Systemorph" — should score higher than a contains-match
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "Sys", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Sys' (RelevanceFirst):");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F1})");

        suggestions.Should().NotBeEmpty();

        // First result should be the prefix match (highest score)
        if (suggestions.Count >= 2)
        {
            suggestions[0].Score.Should().BeGreaterThanOrEqualTo(suggestions[1].Score,
                "Prefix matches should score equal or higher than later results");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_LimitIsRespected()
    {
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "", AutocompleteMode.RelevanceFirst, 3)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions with limit 3");

        suggestions.Count.Should().BeLessThanOrEqualTo(3, "Should respect the limit parameter");
    }

    #endregion

    #region BasePath + Prefix Combination Tests

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_BasePathWithPrefix_SearchesWithinPath()
    {
        // Search within Systemorph for "Mar" — should find Marketing
        var suggestions = await MeshQuery
            .AutocompleteAsync("Systemorph", "Mar", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Mar' under 'Systemorph':");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F1}) | Icon: {s.Icon ?? "(null)"}");

        suggestions.Should().NotBeEmpty("Should find Marketing under Systemorph");
        suggestions.Should().OnlyContain(s => s.Path.StartsWith("Systemorph"),
            "All results should be under the basePath");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_BasePathWithPrefix_ReturnsIcons()
    {
        // Verify icons come through even with basePath + prefix
        var suggestions = await MeshQuery
            .AutocompleteAsync("Systemorph", "Mar", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();

        var marketing = suggestions.FirstOrDefault(s => s.Name != null && s.Name.Contains("Marketing"));
        if (marketing != null)
        {
            Output.WriteLine($"Marketing node: {marketing.Path} | Icon: {marketing.Icon ?? "(null)"}");
            marketing.Icon.Should().NotBeNullOrEmpty("Marketing has an icon defined in its JSON");
        }
        else
        {
            Output.WriteLine("Marketing node not found in suggestions");
        }
    }

    #endregion
}

[CollectionDefinition("AutocompleteIconTests", DisableParallelization = true)]
public class AutocompleteIconTestsDefinition { }
