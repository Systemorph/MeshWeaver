using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Completion;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Completion;
using Xunit;

#pragma warning disable CS1591

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the hierarchical autocomplete system.
/// Tests completion stages: Prefix → AddressId → Reference
/// </summary>
public class AutocompleteServiceTest
{
    #region AgentContext.FromUnifiedPath Tests

    [Fact]
    public void AgentContext_FromUnifiedPath_Empty_ReturnsEmptyContext()
    {
        // act
        var context = AgentContext.FromUnifiedPath("");

        // assert
        context.Address.Should().BeNull();
        context.LayoutArea.Should().BeNull();
        context.Context.Should().Be("");
    }

    [Fact]
    public void AgentContext_FromUnifiedPath_ReservedPrefix_Agent_NoAddress()
    {
        // act
        var context = AgentContext.FromUnifiedPath("agent/InsuranceAgent");

        // assert
        context.Address.Should().BeNull("agent/ is a reserved prefix with no address");
        context.Context.Should().Be("agent/InsuranceAgent");
    }

    [Fact]
    public void AgentContext_FromUnifiedPath_ReservedPrefix_Model_NoAddress()
    {
        // act
        var context = AgentContext.FromUnifiedPath("model/claude-opus");

        // assert
        context.Address.Should().BeNull("model/ is a reserved prefix with no address");
        context.Context.Should().Be("model/claude-opus");
    }

    [Fact]
    public void AgentContext_FromUnifiedPath_StandardPrefix_Area()
    {
        // act
        var context = AgentContext.FromUnifiedPath("area/pricing/MS-2024/Summary");

        // assert
        context.Address.Should().NotBeNull();
        context.Address!.Type.Should().Be("pricing");
        context.Address.Id.Should().Be("MS-2024");
        context.LayoutArea.Should().NotBeNull();
        context.LayoutArea!.Area.Should().Be("Summary");
        context.Context.Should().Be("area/pricing/MS-2024/Summary");
    }

    [Fact]
    public void AgentContext_FromUnifiedPath_StandardPrefix_Data()
    {
        // act
        var context = AgentContext.FromUnifiedPath("data/host/1/TestCollection/entity1");

        // assert
        context.Address.Should().NotBeNull();
        context.Address!.Type.Should().Be("host");
        context.Address.Id.Should().Be("1");
        context.LayoutArea.Should().NotBeNull();
        context.LayoutArea!.Area.Should().Be("TestCollection");
        context.LayoutArea!.Id.Should().Be("entity1");
    }

    [Fact]
    public void AgentContext_FromUnifiedPath_CustomPrefix_NoStandardPrefix()
    {
        // When no standard prefix (area/, data/, content/) is present,
        // the first segment is the addressType
        var context = AgentContext.FromUnifiedPath("pricing/MS-2024/Summary");

        // assert
        context.Address.Should().NotBeNull();
        context.Address!.Type.Should().Be("pricing");
        context.Address.Id.Should().Be("MS-2024");
        context.LayoutArea.Should().NotBeNull();
        context.LayoutArea!.Area.Should().Be("Summary");
    }

    [Fact]
    public void AgentContext_FromUnifiedPath_NestedAreaId()
    {
        // act
        var context = AgentContext.FromUnifiedPath("area/pricing/MS-2024/Details/risk123/subrisk456");

        // assert
        context.LayoutArea.Should().NotBeNull();
        context.LayoutArea!.Area.Should().Be("Details");
        context.LayoutArea.Id.Should().Be("risk123/subrisk456");
    }

    #endregion

    #region AgentContext.ToUnifiedPath Tests

    [Fact]
    public void AgentContext_ToUnifiedPath_WithContextSet_ReturnsContext()
    {
        // arrange
        var context = new AgentContext { Context = "pricing/MS-2024/Summary" };

        // act
        var path = context.ToUnifiedPath();

        // assert
        path.Should().Be("pricing/MS-2024/Summary");
    }

    [Fact]
    public void AgentContext_ToUnifiedPath_FromAddressAndLayoutArea()
    {
        // arrange
        var context = new AgentContext
        {
            Address = new Messaging.Address("pricing", "MS-2024"),
            LayoutArea = new LayoutAreaReference("Summary")
        };

        // act
        var path = context.ToUnifiedPath();

        // assert
        path.Should().Be("pricing/MS-2024/Summary");
    }

    [Fact]
    public void AgentContext_ToUnifiedPath_AddressOnly()
    {
        // arrange
        var context = new AgentContext
        {
            Address = new Messaging.Address("pricing", "MS-2024")
        };

        // act
        var path = context.ToUnifiedPath();

        // assert
        path.Should().Be("pricing/MS-2024");
    }

    [Fact]
    public void AgentContext_ToUnifiedPath_NoAddress_ReturnsNull()
    {
        // arrange
        var context = new AgentContext();

        // act
        var path = context.ToUnifiedPath();

        // assert
        path.Should().BeNull();
    }

    #endregion

    #region AutocompleteService Tests with Providers

    [Fact]
    public async Task AutocompleteService_GetCompletions_AggregatesProviders()
    {
        // arrange - create service with model provider
        var modelProvider = new ModelAutocompleteProvider();
        modelProvider.SetAvailableModels(["claude-opus"]);
        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, [modelProvider]);

        // act
        var results = await service.GetCompletionsAsync("@model/", 20, TestContext.Current.CancellationToken);

        // assert - should return model from provider
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Label == "@model/claude-opus");
    }

    [Fact]
    public async Task AutocompleteService_GetCompletions_EmptyProviders_ReturnsEmpty()
    {
        // arrange
        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, []);

        // act
        var results = await service.GetCompletionsAsync("@", 20, TestContext.Current.CancellationToken);

        // assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task AutocompleteService_GetCompletions_FuzzyMatches()
    {
        // arrange
        var modelProvider = new ModelAutocompleteProvider();
        modelProvider.SetAvailableModels(["TestModel"]);
        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, [modelProvider]);

        // act - partial query should fuzzy match
        var results = await service.GetCompletionsAsync("@model/Test", 20, TestContext.Current.CancellationToken);

        // assert
        results.Should().Contain(r => r.Label == "@model/TestModel");
    }

    [Fact]
    public async Task AutocompleteService_GetCompletions_MultipleProviders()
    {
        // arrange
        var modelProvider = new ModelAutocompleteProvider();
        modelProvider.SetAvailableModels(["claude-opus"]);

        var commandProvider = new MockAutocompleteProvider([
            new AutocompleteItem("@cmd/test", "@cmd/test", "A test command", "Commands", 0, AutocompleteKind.Other)
        ]);

        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, [modelProvider, commandProvider]);

        // act
        var results = await service.GetCompletionsAsync("@", 20, TestContext.Current.CancellationToken);

        // assert - should have items from both providers
        results.Should().Contain(r => r.Label == "@cmd/test");
        results.Should().Contain(r => r.Label == "@model/claude-opus");
    }

    #endregion

    #region AutocompleteRequest/Response Tests

    [Fact]
    public async Task AutocompleteService_GetCompletionsAsync_Request_ReturnsResponse()
    {
        // arrange
        var modelProvider = new ModelAutocompleteProvider();
        modelProvider.SetAvailableModels(["claude-opus"]);
        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, [modelProvider]);
        var request = new AutocompleteRequest("@model/", null);

        // act
        var response = await service.GetCompletionsAsync(request, TestContext.Current.CancellationToken);

        // assert
        response.Should().NotBeNull();
        response.Items.Should().NotBeEmpty();
        response.Items.Should().Contain(i => i.Label == "@model/claude-opus");
    }

    [Fact]
    public async Task AutocompleteService_GetCompletionsAsync_Request_WithContext()
    {
        // arrange
        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, []);
        var request = new AutocompleteRequest("@area/", "pricing/MS-2024");

        // act
        var response = await service.GetCompletionsAsync(request, TestContext.Current.CancellationToken);

        // assert
        response.Should().NotBeNull();
    }

    #endregion

    #region MeshCatalogAutocompleteProvider Tests

    [Fact]
    public async Task MeshCatalogAutocompleteProvider_GetItems_ReturnsNodes()
    {
        // arrange
        var mockCatalog = new MockMeshCatalog(
        [
            new Mesh.MeshNode("pricing")
            {
                Name = "Pricing",
                DisplayOrder = 100
            },
            new Mesh.MeshNode("northwind")
            {
                Name = "Northwind",
                DisplayOrder = 200
            }
        ]);
        var provider = new MeshCatalogAutocompleteProvider(mockCatalog);

        // act
        var items = await provider.GetItemsAsync("@", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // assert - includes catalog nodes + reserved prefixes (@agent/, @model/)
        items.Should().HaveCount(4);
        items.Should().Contain(i => i.Label == "@pricing/");
        items.Should().Contain(i => i.Label == "@northwind/");
        items.Should().Contain(i => i.Label == "@agent/");
        items.Should().Contain(i => i.Label == "@model/");

        var pricingItem = items.First(i => i.Label == "@pricing/");
        pricingItem.Description.Should().Be("Pricing");
    }

    [Fact]
    public async Task MeshCatalogAutocompleteProvider_WithService_FuzzyMatches()
    {
        // arrange
        var mockCatalog = new MockMeshCatalog(
        [
            new Mesh.MeshNode("pricing") { Name = "Pricing" }
        ]);
        var catalogProvider = new MeshCatalogAutocompleteProvider(mockCatalog);
        var fuzzyScorer = new FuzzyScorer();
        var service = new AutocompleteService(fuzzyScorer, [catalogProvider]);

        // act - "pri" should fuzzy match "pricing"
        var results = await service.GetCompletionsAsync("@pri", 20, TestContext.Current.CancellationToken);

        // assert
        results.Should().Contain(r => r.Label == "@pricing/");
    }

    #endregion

    #region Provider Tests

    [Fact]
    public async Task ModelAutocompleteProvider_GetItems_ReturnsModels()
    {
        // arrange
        var provider = new ModelAutocompleteProvider();
        provider.SetAvailableModels(["model1", "model2", "model3"]);

        // act
        var items = await provider.GetItemsAsync("", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // assert
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Label == "@model/model1");
        items.Should().Contain(i => i.Label == "@model/model2");
        items.Should().Contain(i => i.Label == "@model/model3");
    }

    [Fact]
    public async Task ModelAutocompleteProvider_NoModels_ReturnsEmpty()
    {
        // arrange
        var provider = new ModelAutocompleteProvider();
        // Don't set any models

        // act
        var items = await provider.GetItemsAsync("", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // assert
        items.Should().BeEmpty();
    }

    #endregion

    #region Helper Classes

    private class MockAutocompleteProvider(IReadOnlyList<AutocompleteItem> items) : IAutocompleteProvider
    {
        public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
            string query,
            string? contextPath = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var item in items)
            {
                yield return item;
            }
        }
    }


    private class MockMeshCatalog(System.Collections.Generic.IReadOnlyList<Mesh.MeshNode> nodes) : Mesh.Services.IMeshCatalog
    {
        public Mesh.MeshConfiguration Configuration => new(nodes.ToDictionary(n => n.Path));

        public Task<Mesh.MeshNode?> GetNodeAsync(Messaging.Address address) => Task.FromResult<Mesh.MeshNode?>(null);
        public Task UpdateAsync(Mesh.MeshNode node) => Task.CompletedTask;
        public Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
        {
            throw new System.NotImplementedException();
        }

        public Task<MeshNode> CreateTransientNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
        {
            throw new System.NotImplementedException();
        }

        public Task<MeshNode> ConfirmNodeAsync(string path, CancellationToken ct = default)
        {
            throw new System.NotImplementedException();
        }

        public Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
        {
            throw new System.NotImplementedException();
        }

        public Task<Mesh.Services.AddressResolution?> ResolvePathAsync(string path) => Task.FromResult(ResolvePath(path));

        public Mesh.Services.AddressResolution? ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var segments = path.TrimStart('/').Split('/');
            if (segments.Length == 0)
                return null;

            // Score-based matching
            var bestMatch = nodes
                .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score)
                .FirstOrDefault();

            if (bestMatch.Node == null)
                return null;

            var matchedSegments = bestMatch.Score;
            var remainder = matchedSegments < segments.Length
                ? string.Join("/", segments.Skip(matchedSegments))
                : null;

            return new Mesh.Services.AddressResolution(bestMatch.Node.Path, remainder);
        }

        private static int ScoreMatch(Mesh.MeshNode node, string[] pathSegments)
        {
            var nodeSegments = node.Segments;
            if (nodeSegments.Count > pathSegments.Length)
                return 0;

            for (int i = 0; i < nodeSegments.Count; i++)
            {
                if (!nodeSegments[i].Equals(pathSegments[i], System.StringComparison.OrdinalIgnoreCase))
                    return 0;
            }

            return nodeSegments.Count;
        }

        public Mesh.Services.IPersistenceService Persistence => throw new System.NotImplementedException();
#pragma warning disable CS1998 // Async method lacks 'await' operators
        public async System.Collections.Generic.IAsyncEnumerable<Mesh.MeshNode> QueryAsync(string? parentPath, string? query = null, int? maxResults = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
#pragma warning restore CS1998
        {
            yield break;
        }
    }

    #endregion
}
