using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

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
        var reference = ContentReference.Parse("area:pricing/test-company-2024/Overview");

        // assert
        var areaRef = reference.Should().BeOfType<LayoutAreaContentReference>().Subject;
        areaRef.AddressType.Should().Be("pricing");
        areaRef.AddressId.Should().Be("test-company-2024");
        areaRef.AreaName.Should().Be("Overview");
        areaRef.AreaId.Should().BeNull();
    }

    [Fact]
    public void ContentReference_Parse_AreaPath_WithId()
    {
        // arrange & act
        var reference = ContentReference.Parse("area:pricing/test-company-2024/Overview/details");

        // assert
        var areaRef = reference.Should().BeOfType<LayoutAreaContentReference>().Subject;
        areaRef.AddressType.Should().Be("pricing");
        areaRef.AddressId.Should().Be("test-company-2024");
        areaRef.AreaName.Should().Be("Overview");
        areaRef.AreaId.Should().Be("details");
    }

    [Fact]
    public void ContentReference_Parse_ContentPath_Simple()
    {
        // arrange & act
        var reference = ContentReference.Parse("content:host/1/Submissions/folder/file.xlsx");

        // assert
        var fileRef = reference.Should().BeOfType<FileContentReference>().Subject;
        fileRef.AddressType.Should().Be("host");
        fileRef.AddressId.Should().Be("1");
        fileRef.Collection.Should().Be("Submissions");
        fileRef.FilePath.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().BeNull();
    }

    [Fact]
    public void ContentReference_Parse_ContentPath_WithPartition()
    {
        // arrange & act
        var reference = ContentReference.Parse("content:host/1/Submissions@MS-2024/folder/file.xlsx");

        // assert
        var fileRef = reference.Should().BeOfType<FileContentReference>().Subject;
        fileRef.AddressType.Should().Be("host");
        fileRef.AddressId.Should().Be("1");
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
            "area:pricing/test-company-2024/Overview",
            "area:pricing/test-company-2024/Overview/details"
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
            "content:host/1/Submissions/file.xlsx",
            "content:host/1/Submissions@MS-2024/folder/file.xlsx"
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
    [InlineData("content:host/1/collection")] // Missing file path
    [InlineData("area:")]
    [InlineData("area:host/1")] // Missing area name
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
        var path = "area:host/1/TestArea";

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
        var path = $"area:host/1/TestArea/{TestPricingId}";

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
        await File.WriteAllTextAsync(testFilePath, "Content via content: prefix", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "content:host/1/TestFiles/content-test.txt";

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
        await File.WriteAllTextAsync(testFilePath, "Hello, World!\nThis is a test file.\nLine 3.", TestContext.Current.CancellationToken);

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
        await File.WriteAllTextAsync(testFilePath, "Line 1\nLine 2\nLine 3\nLine 4\nLine 5", TestContext.Current.CancellationToken);

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
        await File.WriteAllTextAsync(testFilePath, "Nested file content", TestContext.Current.CancellationToken);

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

    #region Update Tests

    [Fact]
    public async Task UpdateUnifiedReferenceRequest_DataEntity_UpdatesEntity()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = $"data:host/1/TestPricing/{TestPricingId}";

        // First, verify the entity exists
        var getResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);
        getResponse.Message.Error.Should().BeNull();

        // act - update the entity
        var updatedPricing = new TestPricing { Id = TestPricingId, Name = "Updated Pricing", Status = "Draft" };
        var updateResponse = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest(path, updatedPricing),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Error.Should().BeNull();
        updateResponse.Message.Version.Should().BeGreaterThan(0);

        // Verify the update took effect
        var verifyResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);
        var updatedEntity = verifyResponse.Message.Data.Should().BeOfType<TestPricing>().Subject;
        updatedEntity.Name.Should().Be("Updated Pricing");
        updatedEntity.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task UpdateUnifiedReferenceRequest_EmptyPath_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();

        // act
        var response = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest("", new { }),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task UpdateUnifiedReferenceRequest_InvalidPath_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();

        // act
        var response = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest("invalid", new { }),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateUnifiedReferenceRequest_DefaultReference_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "data:host/1";

        // act
        var response = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest(path, new { }),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("default");
    }

    [Fact]
    public async Task UpdateUnifiedReferenceRequest_FileContent_UpdatesFile()
    {
        // arrange - create test file
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "update-test.txt");
        await File.WriteAllTextAsync(testFilePath, "Original content", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "content:host/1/TestFiles/update-test.txt";

            // act
            var response = await client.AwaitResponse(
                new UpdateUnifiedReferenceRequest(path, "Updated content"),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            response.Message.Success.Should().BeTrue();
            response.Message.Error.Should().BeNull();

            // Verify the file was updated
            var fileContent = await File.ReadAllTextAsync(testFilePath, TestContext.Current.CancellationToken);
            fileContent.Should().Be("Updated content");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task UpdateUnifiedReferenceRequest_LayoutArea_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "area:host/1/TestArea";

        // act
        var response = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest(path, new { }),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("not supported");
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_DataEntity_DeletesEntity()
    {
        // arrange - first create an entity to delete
        GetHost();
        var client = GetClient();
        var newEntityId = "delete-test-" + Guid.NewGuid().ToString("N");

        // Create a new entity first
        var createRequest = new DataChangeRequest
        {
            Creations = [new TestPricing { Id = newEntityId, Name = "To Delete", Status = "Active" }],
            ChangedBy = "test"
        };
        await client.AwaitResponse(
            createRequest,
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // Verify it exists
        var path = $"data:host/1/TestPricing/{newEntityId}";
        var getResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);
        getResponse.Message.Data.Should().NotBeNull();

        // act - delete the entity
        var deleteResponse = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        deleteResponse.Message.Success.Should().BeTrue();
        deleteResponse.Message.Error.Should().BeNull();

        // Verify it was deleted
        var verifyResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);
        verifyResponse.Message.Data.Should().BeNull();
    }

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_EmptyPath_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(""),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_DefaultReference_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "data:host/1";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("default");
    }

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_CollectionPath_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "data:host/1/TestPricing";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("Entity ID must be specified");
    }

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_FileContent_DeletesFile()
    {
        // arrange - create test file
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_Delete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "delete-test.txt");
        await File.WriteAllTextAsync(testFilePath, "File to delete", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "content:host/1/TestFiles/delete-test.txt";

            // Verify file exists
            File.Exists(testFilePath).Should().BeTrue();

            // act
            var response = await client.AwaitResponse(
                new DeleteUnifiedReferenceRequest(path),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            response.Message.Success.Should().BeTrue();
            response.Message.Error.Should().BeNull();

            // Verify file was deleted
            File.Exists(testFilePath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_LayoutArea_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "area:host/1/TestArea";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("not supported");
    }

    [Fact]
    public async Task DeleteUnifiedReferenceRequest_NonExistentEntity_ReturnsError()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "data:host/1/TestPricing/nonexistent-entity-id";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("not found");
    }

    #endregion

    #region UnifiedReference Workspace Stream Tests

    [Fact]
    public async Task UnifiedReference_DataPrefix_Collection_ViaGetDataRequest()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "data:host/1/TestPricing";

        // act - use GetDataRequest which correctly handles the UnifiedReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        response.Message.Data.Should().BeOfType<InstanceCollection>();
        var collection = (InstanceCollection)response.Message.Data!;
        collection.Instances.Should().ContainKey(TestPricingId);
    }

    [Fact]
    public async Task UnifiedReference_DataPrefix_Entity_ViaGetDataRequest()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = $"data:host/1/TestPricing/{TestPricingId}";

        // act - use GetDataRequest which correctly handles the UnifiedReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var pricing = response.Message.Data.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public async Task UnifiedReference_AreaPrefix_ViaGetDataRequest()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "area:host/1/TestArea";

        // act - use GetDataRequest which correctly handles the UnifiedReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        // Layout areas return a JSON string
        response.Message.Data.Should().BeOfType<string>();
    }

    [Fact]
    public async Task UnifiedReference_ParsesPathAndUsesRegistry()
    {
        // arrange
        var host = GetHost();
        var registry = host.ServiceProvider.GetRequiredService<IUnifiedReferenceRegistry>();

        // Verify all handlers are registered
        registry.TryGetHandler("data", out _).Should().BeTrue();
        registry.TryGetHandler("area", out _).Should().BeTrue();
        // Note: content handler is only registered if AddContentCollections is called
    }

    [Fact]
    public async Task UnifiedReference_Registry_HandlerRegisteredByAddData()
    {
        // arrange - just calling GetHost() triggers AddData() which should register the handler
        var host = GetHost();
        var registry = host.ServiceProvider.GetRequiredService<IUnifiedReferenceRegistry>();

        // act & assert
        registry.TryGetHandler("data", out var dataHandler).Should().BeTrue();
        dataHandler.Should().BeOfType<DataPrefixHandler>();
    }

    [Fact]
    public async Task UnifiedReference_Registry_AreaHandlerRegisteredByAddLayout()
    {
        // arrange - GetHost() triggers AddLayout() which should register the area handler
        var host = GetHost();
        var registry = host.ServiceProvider.GetRequiredService<IUnifiedReferenceRegistry>();

        // act & assert
        registry.TryGetHandler("area", out var areaHandler).Should().BeTrue();
        areaHandler.Should().BeOfType<AreaPrefixHandler>();
    }

    #endregion

    #region Registry Tests

    [Fact]
    public void DataPrefixHandler_GetAddress_ReturnsCorrectAddress()
    {
        // arrange
        var handler = new DataPrefixHandler();
        var reference = new DataContentReference("pricing", "MS-2024", "PropertyRisk", "risk1");

        // act
        var address = handler.GetAddress(reference);

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
    }

    [Fact]
    public void DataPrefixHandler_CreateWorkspaceReference_ReturnsEntityReference_ForEntityPath()
    {
        // arrange
        var handler = new DataPrefixHandler();
        var reference = new DataContentReference("pricing", "MS-2024", "PropertyRisk", "risk1");

        // act
        var workspaceRef = handler.CreateWorkspaceReference(reference);

        // assert
        var entityRef = workspaceRef.Should().BeOfType<EntityReference>().Subject;
        entityRef.Collection.Should().Be("PropertyRisk");
        entityRef.Id.Should().Be("risk1");
    }

    [Fact]
    public void DataPrefixHandler_CreateWorkspaceReference_ReturnsCollectionReference_ForCollectionPath()
    {
        // arrange
        var handler = new DataPrefixHandler();
        var reference = new DataContentReference("pricing", "MS-2024", "PropertyRisk");

        // act
        var workspaceRef = handler.CreateWorkspaceReference(reference);

        // assert
        var collectionRef = workspaceRef.Should().BeOfType<CollectionReference>().Subject;
        collectionRef.Name.Should().Be("PropertyRisk");
    }

    [Fact]
    public void DataPrefixHandler_CreateWorkspaceReference_ThrowsForDefaultReference()
    {
        // arrange
        var handler = new DataPrefixHandler();
        var reference = new DataContentReference("pricing", "MS-2024");

        // act
        var action = () => handler.CreateWorkspaceReference(reference);

        // assert
        action.Should().Throw<NotSupportedException>().WithMessage("*collection*");
    }

    [Fact]
    public void AreaPrefixHandler_GetAddress_ReturnsCorrectAddress()
    {
        // arrange
        var handler = new AreaPrefixHandler();
        var reference = new LayoutAreaContentReference("pricing", "MS-2024", "Overview", "details");

        // act
        var address = handler.GetAddress(reference);

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
    }

    [Fact]
    public void AreaPrefixHandler_CreateWorkspaceReference_ReturnsLayoutAreaReference()
    {
        // arrange
        var handler = new AreaPrefixHandler();
        var reference = new LayoutAreaContentReference("pricing", "MS-2024", "Overview", "details");

        // act
        var workspaceRef = handler.CreateWorkspaceReference(reference);

        // assert
        var layoutAreaRef = workspaceRef.Should().BeOfType<LayoutAreaReference>().Subject;
        layoutAreaRef.Area.Should().Be("Overview");
        layoutAreaRef.Id.Should().Be("details");
    }

    [Fact]
    public void AreaPrefixHandler_CreateWorkspaceReference_HandlesNullAreaId()
    {
        // arrange
        var handler = new AreaPrefixHandler();
        var reference = new LayoutAreaContentReference("pricing", "MS-2024", "Overview");

        // act
        var workspaceRef = handler.CreateWorkspaceReference(reference);

        // assert
        var layoutAreaRef = workspaceRef.Should().BeOfType<LayoutAreaReference>().Subject;
        layoutAreaRef.Area.Should().Be("Overview");
        layoutAreaRef.Id.Should().BeNull();
    }

    [Fact]
    public void ContentPrefixHandler_GetAddress_ReturnsCorrectAddress()
    {
        // arrange
        var handler = new ContentPrefixHandler();
        var reference = new FileContentReference("host", "1", "Submissions", "folder/file.xlsx", "MS-2024");

        // act
        var address = handler.GetAddress(reference);

        // assert
        address.Type.Should().Be("host");
        address.Id.Should().Be("1");
    }

    [Fact]
    public void ContentPrefixHandler_CreateWorkspaceReference_ReturnsFileReference()
    {
        // arrange
        var handler = new ContentPrefixHandler();
        var reference = new FileContentReference("host", "1", "Submissions", "folder/file.xlsx", "MS-2024");

        // act
        var workspaceRef = handler.CreateWorkspaceReference(reference);

        // assert
        var fileRef = workspaceRef.Should().BeOfType<FileReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.Path.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().Be("MS-2024");
    }

    [Fact]
    public void ContentPrefixHandler_CreateWorkspaceReference_HandlesNullPartition()
    {
        // arrange
        var handler = new ContentPrefixHandler();
        var reference = new FileContentReference("host", "1", "Submissions", "file.xlsx");

        // act
        var workspaceRef = handler.CreateWorkspaceReference(reference);

        // assert
        var fileRef = workspaceRef.Should().BeOfType<FileReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.Path.Should().Be("file.xlsx");
        fileRef.Partition.Should().BeNull();
    }

    [Fact]
    public void UnifiedReferenceRegistry_Register_And_TryGetHandler_Works()
    {
        // arrange
        var registry = new UnifiedReferenceRegistry();
        var handler = new DataPrefixHandler();

        // act
        registry.Register("data", handler);
        var found = registry.TryGetHandler("data", out var retrievedHandler);

        // assert
        found.Should().BeTrue();
        retrievedHandler.Should().BeSameAs(handler);
    }

    [Fact]
    public void UnifiedReferenceRegistry_TryGetHandler_ReturnsFalse_ForUnregisteredPrefix()
    {
        // arrange
        var registry = new UnifiedReferenceRegistry();

        // act
        var found = registry.TryGetHandler("unknown", out var handler);

        // assert
        found.Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void UnifiedReferenceRegistry_IsCaseInsensitive()
    {
        // arrange
        var registry = new UnifiedReferenceRegistry();
        registry.Register("DATA", new DataPrefixHandler());

        // act
        var found = registry.TryGetHandler("data", out var handler);

        // assert
        found.Should().BeTrue();
        handler.Should().NotBeNull();
    }

    [Fact]
    public void UnifiedReferenceRegistry_Prefixes_ReturnsAllRegistered()
    {
        // arrange
        var registry = new UnifiedReferenceRegistry();
        registry.Register("data", new DataPrefixHandler());
        registry.Register("area", new AreaPrefixHandler());
        registry.Register("content", new ContentPrefixHandler());

        // act
        var prefixes = registry.Prefixes.ToList();

        // assert
        prefixes.Should().Contain("data");
        prefixes.Should().Contain("area");
        prefixes.Should().Contain("content");
        prefixes.Should().HaveCount(3);
    }

    [Fact]
    public void UnifiedReferenceRegistry_HandlesColonInPrefix()
    {
        // arrange
        var registry = new UnifiedReferenceRegistry();
        registry.Register("data:", new DataPrefixHandler());

        // act
        var found = registry.TryGetHandler("data", out var handler);

        // assert
        found.Should().BeTrue();
        handler.Should().NotBeNull();
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
