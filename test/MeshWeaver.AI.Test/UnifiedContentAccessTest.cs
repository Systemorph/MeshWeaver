using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the unified content access system using GetDataRequest with UnifiedReference.
/// Tests the path patterns:
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
    public void ContentReference_Parse_ContentPath_Simple()
    {
        // arrange & act
        var reference = ContentReference.Parse("content:Submissions/folder/file.xlsx");

        // assert
        var fileRef = reference.Should().BeOfType<FileContentReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.FilePath.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().BeNull();
    }

    [Fact]
    public void ContentReference_Parse_ContentPath_WithPartition()
    {
        // arrange & act
        var reference = ContentReference.Parse("content:Submissions@MS-2024/folder/file.xlsx");

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
    public void ContentReference_ToPath_ContentReference_RoundTrips()
    {
        var paths = new[]
        {
            "content:Submissions/file.xlsx",
            "content:Submissions@MS-2024/folder/file.xlsx"
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
    [InlineData("content:")]
    [InlineData("content:collection")]
    public void ContentReference_Parse_InvalidPath_ThrowsArgumentException(string invalidPath)
    {
        // arrange & act
        var action = () => ContentReference.Parse(invalidPath);

        // assert
        action.Should().Throw<ArgumentException>();
    }

    #endregion

    #region GetDataRequest with UnifiedReference Tests

    [Fact]
    public async Task GetDataRequest_UnifiedReference_Collection_ReturnsAllEntities()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "data:host/1/TestPricing";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        // Data should be InstanceCollection
        dataResponse.Data.Should().BeOfType<InstanceCollection>();
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_Entity_ReturnsSingleEntity()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = $"data:host/1/TestPricing/{TestPricingId}";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        var pricing = dataResponse.Data.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_DefaultReference_ReturnsDefaultEntity()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "data:host/1";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        var pricing = dataResponse.Data.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_AreaPath_ReturnsLayoutAreaJson()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "area:TestArea";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        // Should be valid JSON string
        var jsonString = dataResponse.Data as string;
        jsonString.Should().NotBeNullOrEmpty();

        // Should be parseable JSON
        var action = () => JsonDocument.Parse(jsonString!);
        action.Should().NotThrow();
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_AreaPath_WithId_ReturnsLayoutAreaJson()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = $"area:TestArea/{TestPricingId}";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_InvalidPath_ReturnsError()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "data:invalid";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_EmptyPath_ReturnsError()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("")),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_ContentPath_ReturnsFileContent()
    {
        // arrange - create test file and configure content provider
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "content-test.txt");
        await File.WriteAllTextAsync(testFilePath, "Content via content: prefix");

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "content:TestFiles/content-test.txt";

            // act - send from client to host
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().BeNull();
            dataResponse.Data.Should().NotBeNull();

            var content = dataResponse.Data as string;
            content.Should().Contain("Content via content: prefix");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    #endregion

    #region File Content Tests

    [Fact]
    public async Task GetDataRequest_UnifiedReference_FileContent_ReturnsFileContent()
    {
        // arrange - create test file and configure content provider
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "test.txt");
        await File.WriteAllTextAsync(testFilePath, "Hello, World!\nThis is a test file.\nLine 3.");

        try
        {
            // Create a host with file content provider
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "data:host/1/TestFiles/test.txt";

            // act - send from client to host
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().BeNull();
            dataResponse.Data.Should().NotBeNull();

            var content = dataResponse.Data as string;
            content.Should().Contain("Hello, World!");
            content.Should().Contain("This is a test file.");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_FileContent_WithNumberOfRows_ReturnsLimitedContent()
    {
        // arrange - create test file
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "multiline.txt");
        await File.WriteAllTextAsync(testFilePath, "Line 1\nLine 2\nLine 3\nLine 4\nLine 5");

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "data:host/1/TestFiles/multiline.txt";

            // act - request only 2 rows
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path) { NumberOfRows = 2 }),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().BeNull();

            var content = dataResponse.Data as string;
            content.Should().Contain("Line 1");
            content.Should().Contain("Line 2");
            content.Should().NotContain("Line 3"); // Should be limited to 2 lines
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_FileContent_NotFound_ReturnsError()
    {
        // arrange
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "data:host/1/TestFiles/nonexistent.txt";

            // act
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().Contain("not found");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_FileContent_SubFolder_ReturnsFileContent()
    {
        // arrange - create test file in subfolder
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(testDir, "subfolder");
        Directory.CreateDirectory(subDir);
        var testFilePath = Path.Combine(subDir, "nested.txt");
        await File.WriteAllTextAsync(testFilePath, "Nested file content");

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "data:host/1/TestFiles/subfolder/nested.txt";

            // act
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().BeNull();

            var content = dataResponse.Data as string;
            content.Should().Contain("Nested file content");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    private IMessageHub? hostWithFileProvider;
    private string? currentTestDir;

    private IMessageHub GetHostWithFileProvider(string testDir)
    {
        // If we already have a host with the same test directory, return it
        if (hostWithFileProvider != null && currentTestDir == testDir)
            return hostWithFileProvider;

        currentTestDir = testDir;
        // Create a new configuration that includes file content provider
        hostWithFileProvider = Router.GetHostedHub(new HostAddress(), ConfigureHostWithFileProvider(testDir));
        return hostWithFileProvider;
    }

    private Func<MessageHubConfiguration, MessageHubConfiguration> ConfigureHostWithFileProvider(string testDir)
    {
        return configuration => configuration
            .AddFileSystemContentCollection("TestFiles", _ => testDir)
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
                    workspace.GetObservable<TestPricing>().Select(p => p.OrderBy(x => x.Id).FirstOrDefault()))
                .WithContentProvider("TestFiles"))
            .AddLayout(layout => layout
                .WithView("TestArea", TestAreaView));
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
                    workspace.GetObservable<TestPricing>().Select(p => p.OrderBy(x => x.Id).FirstOrDefault())))
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
