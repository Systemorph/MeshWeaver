using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the unified content access system (GetContentRequest/GetContentResponse).
/// Tests the new path patterns:
/// - data:addressType/addressId (default data reference)
/// - data:addressType/addressId/collection (collection)
/// - data:addressType/addressId/collection/entityId (entity)
/// - area:areaName/areaId (layout area)
/// </summary>
public class UnifiedContentAccessTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestPricingId = "test-company-2024";

    #region ContentReference Parsing Tests

    [Fact]
    public void ContentReference_Parse_DataPath_DefaultReference()
    {
        // arrange & act
        var reference = ContentReference.Parse("data:pricing/test-company-2024");

        // assert
        var dataRef = reference.Should().BeOfType<DataContentReference>().Subject;
        dataRef.AddressType.Should().Be("pricing");
        dataRef.AddressId.Should().Be("test-company-2024");
        dataRef.Collection.Should().BeNull();
        dataRef.EntityId.Should().BeNull();
        dataRef.IsDefaultReference.Should().BeTrue();
        dataRef.IsCollectionReference.Should().BeFalse();
        dataRef.IsEntityReference.Should().BeFalse();
    }

    [Fact]
    public void ContentReference_Parse_DataPath_CollectionReference()
    {
        // arrange & act
        var reference = ContentReference.Parse("data:pricing/test-company-2024/PropertyRisk");

        // assert
        var dataRef = reference.Should().BeOfType<DataContentReference>().Subject;
        dataRef.AddressType.Should().Be("pricing");
        dataRef.AddressId.Should().Be("test-company-2024");
        dataRef.Collection.Should().Be("PropertyRisk");
        dataRef.EntityId.Should().BeNull();
        dataRef.IsDefaultReference.Should().BeFalse();
        dataRef.IsCollectionReference.Should().BeTrue();
        dataRef.IsEntityReference.Should().BeFalse();
    }

    [Fact]
    public void ContentReference_Parse_DataPath_EntityReference()
    {
        // arrange & act
        var reference = ContentReference.Parse("data:pricing/test-company-2024/PropertyRisk/risk1");

        // assert
        var dataRef = reference.Should().BeOfType<DataContentReference>().Subject;
        dataRef.AddressType.Should().Be("pricing");
        dataRef.AddressId.Should().Be("test-company-2024");
        dataRef.Collection.Should().Be("PropertyRisk");
        dataRef.EntityId.Should().Be("risk1");
        dataRef.IsDefaultReference.Should().BeFalse();
        dataRef.IsCollectionReference.Should().BeFalse();
        dataRef.IsEntityReference.Should().BeTrue();
    }

    [Fact]
    public void ContentReference_Parse_AreaPath_WithoutId()
    {
        // arrange & act
        var reference = ContentReference.Parse("area:Overview");

        // assert
        var areaRef = reference.Should().BeOfType<LayoutAreaContentReference>().Subject;
        areaRef.AreaName.Should().Be("Overview");
        areaRef.AreaId.Should().BeNull();
    }

    [Fact]
    public void ContentReference_Parse_AreaPath_WithId()
    {
        // arrange & act
        var reference = ContentReference.Parse("area:Overview/test-company-2024");

        // assert
        var areaRef = reference.Should().BeOfType<LayoutAreaContentReference>().Subject;
        areaRef.AreaName.Should().Be("Overview");
        areaRef.AreaId.Should().Be("test-company-2024");
    }

    [Fact]
    public void ContentReference_Parse_FilePath_Simple()
    {
        // arrange & act
        var reference = ContentReference.Parse("Submissions:folder/file.xlsx");

        // assert
        var fileRef = reference.Should().BeOfType<FileContentReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.FilePath.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().BeNull();
    }

    [Fact]
    public void ContentReference_Parse_FilePath_WithPartition()
    {
        // arrange & act
        var reference = ContentReference.Parse("Submissions@MS-2024:folder/file.xlsx");

        // assert
        var fileRef = reference.Should().BeOfType<FileContentReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.FilePath.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().Be("MS-2024");
    }

    [Fact]
    public void ContentReference_ToPath_DataReference_RoundTrips()
    {
        var paths = new[]
        {
            "data:pricing/test-company-2024",
            "data:pricing/test-company-2024/PropertyRisk",
            "data:pricing/test-company-2024/PropertyRisk/risk1"
        };

        foreach (var path in paths)
        {
            var reference = ContentReference.Parse(path);
            reference.ToPath().Should().Be(path);
        }
    }

    [Fact]
    public void ContentReference_ToPath_AreaReference_RoundTrips()
    {
        var paths = new[]
        {
            "area:Overview",
            "area:Overview/test-company-2024"
        };

        foreach (var path in paths)
        {
            var reference = ContentReference.Parse(path);
            reference.ToPath().Should().Be(path);
        }
    }

    [Fact]
    public void ContentReference_ToPath_FileReference_RoundTrips()
    {
        var paths = new[]
        {
            "Submissions:file.xlsx",
            "Submissions@MS-2024:folder/file.xlsx"
        };

        foreach (var path in paths)
        {
            var reference = ContentReference.Parse(path);
            reference.ToPath().Should().Be(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("data:")]
    [InlineData("data:pricing")]
    public void ContentReference_Parse_InvalidPath_ThrowsArgumentException(string invalidPath)
    {
        // arrange & act
        var action = () => ContentReference.Parse(invalidPath);

        // assert
        action.Should().Throw<ArgumentException>();
    }

    #endregion

    #region GetContentRequest Handler Tests

    [Fact]
    public async Task GetContentRequest_DataPath_Collection_ReturnsAllEntities()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = $"data:host/1/TestPricing";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.Data);
        contentResponse.Error.Should().BeNull();
        contentResponse.Content.Should().NotBeNull();

        // Content may be typed (InstanceCollection) or JsonElement depending on registration
        var content = contentResponse.Content;
        content.Should().BeOfType<InstanceCollection>();
    }

    [Fact]
    public async Task GetContentRequest_DataPath_Entity_ReturnsSingleEntity()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = $"data:host/1/TestPricing/{TestPricingId}";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.Data);
        contentResponse.Error.Should().BeNull();
        contentResponse.Content.Should().NotBeNull();

        // Content may be typed (TestPricing) or JsonElement depending on registration
        var pricing = contentResponse.Content.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public async Task GetContentRequest_DataPath_DefaultReference_ReturnsDefaultEntity()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "data:host/1";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.Data);
        contentResponse.Error.Should().BeNull();
        contentResponse.Content.Should().NotBeNull();

        // Content may be typed (TestPricing) or JsonElement depending on registration
        var pricing = contentResponse.Content.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public async Task GetContentRequest_AreaPath_ReturnsLayoutAreaJson()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "area:TestArea";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.LayoutArea);
        contentResponse.Error.Should().BeNull();
        contentResponse.Content.Should().NotBeNull();

        // Should be valid JSON string
        var jsonString = contentResponse.Content as string;
        jsonString.Should().NotBeNullOrEmpty();

        // Should be parseable JSON
        var action = () => JsonDocument.Parse(jsonString!);
        action.Should().NotThrow();
    }

    [Fact]
    public async Task GetContentRequest_AreaPath_WithId_ReturnsLayoutAreaJson()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = $"area:TestArea/{TestPricingId}";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.LayoutArea);
        contentResponse.Error.Should().BeNull();
        contentResponse.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task GetContentRequest_InvalidPath_ReturnsError()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "data:invalid";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.Error);
        contentResponse.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetContentRequest_EmptyPath_ReturnsError()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(""),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.Error);
        contentResponse.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task GetContentRequest_FilePath_ReturnsNotImplementedError()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "Submissions:file.xlsx";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetContentRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var contentResponse = response.Message;
        contentResponse.Type.Should().Be(ContentType.Error);
        contentResponse.Error.Should().Contain("not yet implemented");
    }

    #endregion

    #region GetDefaultDataRequest Tests

    [Fact]
    public async Task GetDefaultDataRequest_WithConfiguredDefault_ReturnsData()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDefaultDataRequest(),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();

        var pricing = response.Message.Data as TestPricing;
        pricing.Should().NotBeNull();
        pricing!.Id.Should().Be(TestPricingId);
    }

    #endregion

    #region Test Configuration

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data
                .AddSource(source => source
                    .WithType<TestPricing>(t => t
                        .WithInitialData(_ => Task.FromResult(new List<TestPricing>
                        {
                            new() { Id = TestPricingId, Name = "Test Pricing", Status = "Active" },
                            new() { Id = "test-company-2025", Name = "Test Pricing 2", Status = "Draft" }
                        }.AsEnumerable())))
                    .WithType<TestPropertyRisk>(t => t
                        .WithInitialData(_ => Task.FromResult(new List<TestPropertyRisk>
                        {
                            new() { Id = "risk1", PricingId = TestPricingId, Location = "NYC", Value = 1000000m },
                            new() { Id = "risk2", PricingId = TestPricingId, Location = "LA", Value = 2000000m }
                        }.AsEnumerable()))))
                .WithDefaultDataReference(workspace =>
                    workspace.GetObservable<TestPricing>().Select(p => p.FirstOrDefault())))
            .AddLayout(layout => layout
                .WithView("TestArea", TestAreaView));
    }

    private static UiControl TestAreaView(LayoutAreaHost host, RenderingContext ctx)
    {
        return Controls.Html("<div>Test Area Content</div>");
    }

    #endregion

    #region Test Domain Models

    /// <summary>
    /// Test pricing entity for unified content access tests.
    /// </summary>
    public record TestPricing
    {
        [Key]
        public required string Id { get; init; }
        public string? Name { get; init; }
        public string? Status { get; init; }
    }

    /// <summary>
    /// Test property risk entity for unified content access tests.
    /// </summary>
    public record TestPropertyRisk
    {
        [Key]
        public required string Id { get; init; }
        public string? PricingId { get; init; }
        public string? Location { get; init; }
        public decimal Value { get; init; }
    }

    #endregion
}
