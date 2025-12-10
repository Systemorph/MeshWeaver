using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the unified content access system using GetDataRequest with UnifiedReference.
/// Tests the path patterns:
/// - addressType/addressId/data (default data reference)
/// - addressType/addressId/data/collection (collection)
/// - addressType/addressId/data/collection/entityId (entity)
/// - addressType/addressId/areaName (layout area, area is default)
/// - addressType/addressId/content/collection/path (content)
/// </summary>
public class UnifiedContentAccessTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestPricingId = "test-company-2024";

    #region UnifiedPathHandler Parsing Tests

    [Fact]
    public void DataPathHandler_Parse_DefaultReference()
    {
        // arrange & act
        var handler = new DataPathHandler();
        var (address, reference) = handler.Parse("pricing", "test-company-2024", "");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("test-company-2024");
        var dataRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataRef.Path.Should().Be("");
    }

    [Fact]
    public void DataPathHandler_Parse_CollectionReference()
    {
        // arrange & act
        var handler = new DataPathHandler();
        var (address, reference) = handler.Parse("pricing", "test-company-2024", "PropertyRisk");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("test-company-2024");
        var dataRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataRef.Path.Should().Be("PropertyRisk");
    }

    [Fact]
    public void DataPathHandler_Parse_EntityReference()
    {
        // arrange & act
        var handler = new DataPathHandler();
        var (address, reference) = handler.Parse("pricing", "test-company-2024", "PropertyRisk/risk1");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("test-company-2024");
        var dataRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataRef.Path.Should().Be("PropertyRisk/risk1");
    }

    [Fact]
    public void AreaPathHandler_Parse_WithoutId()
    {
        // arrange & act
        var handler = new AreaPathHandler();
        var (address, reference) = handler.Parse("pricing", "test-company-2024", "Overview");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("test-company-2024");
        var areaRef = reference.Should().BeOfType<LayoutAreaReference>().Subject;
        areaRef.Area.Should().Be("Overview");
        areaRef.Id.Should().BeNull();
    }

    [Fact]
    public void AreaPathHandler_Parse_WithId()
    {
        // arrange & act
        var handler = new AreaPathHandler();
        var (address, reference) = handler.Parse("pricing", "test-company-2024", "Overview/details");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("test-company-2024");
        var areaRef = reference.Should().BeOfType<LayoutAreaReference>().Subject;
        areaRef.Area.Should().Be("Overview");
        areaRef.Id.Should().Be("details");
    }

    [Fact]
    public void ContentPathHandler_Parse_Simple()
    {
        // arrange & act
        var handler = new ContentPathHandler();
        var (address, reference) = handler.Parse("host", "1", "Submissions/folder/file.xlsx");

        // assert
        address.Type.Should().Be("host");
        address.Id.Should().Be("1");
        var fileRef = reference.Should().BeOfType<FileReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.Path.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().BeNull();
    }

    [Fact]
    public void ContentPathHandler_Parse_WithPartition()
    {
        // arrange & act
        var handler = new ContentPathHandler();
        var (address, reference) = handler.Parse("host", "1", "Submissions@MS-2024/folder/file.xlsx");

        // assert
        address.Type.Should().Be("host");
        address.Id.Should().Be("1");
        var fileRef = reference.Should().BeOfType<FileReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.Path.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().Be("MS-2024");
    }

    [Fact]
    public void AreaPathHandler_Parse_EmptyPath_ReturnsNullArea()
    {
        // arrange & act
        var handler = new AreaPathHandler();
        var (address, reference) = handler.Parse("host", "1", "");

        // assert
        address.Type.Should().Be("host");
        address.Id.Should().Be("1");
        var areaRef = reference.Should().BeOfType<LayoutAreaReference>().Subject;
        areaRef.Area.Should().BeNull();
        areaRef.Id.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("collection")] // Missing file path
    public void ContentPathHandler_Parse_InvalidPath_ThrowsArgumentException(string invalidPath)
    {
        var handler = new ContentPathHandler();
        var action = () => handler.Parse("host", "1", invalidPath);
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
        var path = "host/1/data/TestPricing";

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
        var path = $"host/1/data/TestPricing/{TestPricingId}";

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
        var path = "host/1/data";

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
        var path = "host/1/TestArea"; // area is default, no keyword needed

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
        var path = $"host/1/TestArea/{TestPricingId}"; // area is default, no keyword needed

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
        var path = "invalid"; // Single segment is invalid

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
        await File.WriteAllTextAsync(testFilePath, "Content via content keyword", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();
            var path = "host/1/content/TestFiles/content-test.txt";

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
            content.Should().Contain("Content via content keyword");
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
            var path = "host/1/data/TestFiles/test.txt";

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
            var path = "host/1/data/TestFiles/multiline.txt";

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
            var path = "host/1/data/TestFiles/nonexistent.txt";

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
            var path = "host/1/data/TestFiles/subfolder/nested.txt";

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
        var path = $"host/1/data/TestPricing/{TestPricingId}";

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
        var path = "host/1/data";

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
            var path = "host/1/content/TestFiles/update-test.txt";

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
        var path = "host/1/TestArea"; // area is default

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
        var path = $"host/1/data/TestPricing/{newEntityId}";
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
        var path = "host/1/data";

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
        var path = "host/1/data/TestPricing";

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
            var path = "host/1/content/TestFiles/delete-test.txt";

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
        var path = "host/1/TestArea"; // area is default

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
        var path = "host/1/data/TestPricing/nonexistent-entity-id";

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
    public async Task UnifiedReference_DataKeyword_Collection_ViaGetDataRequest()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "host/1/data/TestPricing";

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
    public async Task UnifiedReference_DataKeyword_Entity_ViaGetDataRequest()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = $"host/1/data/TestPricing/{TestPricingId}";

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
    public async Task UnifiedReference_AreaDefault_ViaGetDataRequest()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "host/1/TestArea"; // area is default, no keyword needed

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
    public void UnifiedPathRegistry_ParsesPathAndUsesRegistry()
    {
        // arrange - test the registry directly without needing mesh infrastructure
        var registry = new UnifiedPathRegistry();
        registry.Register("data", new DataPathHandler());
        registry.Register("area", new AreaPathHandler());
        registry.Register("content", new ContentPathHandler());

        // act & assert - verify registry can resolve paths with new format
        // Format: addressType/addressId/keyword/remainingPath
        registry.TryResolve("pricing/MS-2024/data/TestPricing", out var dataAddr, out var dataRef).Should().BeTrue();
        dataAddr!.Type.Should().Be("pricing");
        dataRef.Should().BeOfType<DataPathReference>();

        // Area with explicit keyword
        registry.TryResolve("pricing/MS-2024/area/Overview", out var areaAddr, out var areaRef).Should().BeTrue();
        areaAddr!.Type.Should().Be("pricing");
        areaRef.Should().BeOfType<LayoutAreaReference>();

        // Area as default (no keyword)
        registry.TryResolve("pricing/MS-2024/Overview", out var areaAddr2, out var areaRef2).Should().BeTrue();
        areaAddr2!.Type.Should().Be("pricing");
        areaRef2.Should().BeOfType<LayoutAreaReference>();

        registry.TryResolve("pricing/MS-2024/content/Submissions/file.xlsx", out var contentAddr, out var contentRef).Should().BeTrue();
        contentAddr!.Type.Should().Be("pricing");
        contentRef.Should().BeOfType<FileReference>();
    }

    [Fact]
    public void UnifiedPathRegistry_BuiltInHandlersWork()
    {
        // arrange - test built-in handlers directly
        var registry = new UnifiedPathRegistry();
        registry.Register("data", new DataPathHandler());
        registry.Register("area", new AreaPathHandler());
        registry.Register("content", new ContentPathHandler());

        // act & assert
        registry.Keywords.Should().Contain("data");
        registry.Keywords.Should().Contain("area");
        registry.Keywords.Should().Contain("content");
    }

    #endregion

    #region Registry Tests

    [Fact]
    public void DataPathHandler_Parse_ReturnsDataPathReference_ForEntityPath()
    {
        // arrange
        var handler = new DataPathHandler();

        // act
        var (address, reference) = handler.Parse("pricing", "MS-2024", "PropertyRisk/risk1");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
        var dataPathRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataPathRef.Path.Should().Be("PropertyRisk/risk1");
    }

    [Fact]
    public void DataPathHandler_Parse_ReturnsDataPathReference_ForCollectionPath()
    {
        // arrange
        var handler = new DataPathHandler();

        // act
        var (address, reference) = handler.Parse("pricing", "MS-2024", "PropertyRisk");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
        var dataPathRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataPathRef.Path.Should().Be("PropertyRisk");
    }

    [Fact]
    public void DataPathHandler_Parse_ReturnsEmptyDataPathReference_ForDefaultPath()
    {
        // arrange
        var handler = new DataPathHandler();

        // act
        var (address, reference) = handler.Parse("pricing", "MS-2024", "");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
        var dataPathRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataPathRef.Path.Should().Be("");
    }

    [Fact]
    public void AreaPathHandler_Parse_ReturnsCorrectAddressAndReference()
    {
        // arrange
        var handler = new AreaPathHandler();

        // act
        var (address, reference) = handler.Parse("pricing", "MS-2024", "Overview/details");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
        var layoutAreaRef = reference.Should().BeOfType<LayoutAreaReference>().Subject;
        layoutAreaRef.Area.Should().Be("Overview");
        layoutAreaRef.Id.Should().Be("details");
    }

    [Fact]
    public void AreaPathHandler_Parse_HandlesNullAreaId()
    {
        // arrange
        var handler = new AreaPathHandler();

        // act
        var (address, reference) = handler.Parse("pricing", "MS-2024", "Overview");

        // assert
        address.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
        var layoutAreaRef = reference.Should().BeOfType<LayoutAreaReference>().Subject;
        layoutAreaRef.Area.Should().Be("Overview");
        layoutAreaRef.Id.Should().BeNull();
    }

    [Fact]
    public void ContentPathHandler_Parse_ReturnsCorrectAddressAndReference()
    {
        // arrange
        var handler = new ContentPathHandler();

        // act
        var (address, reference) = handler.Parse("host", "1", "Submissions@MS-2024/folder/file.xlsx");

        // assert
        address.Type.Should().Be("host");
        address.Id.Should().Be("1");
        var fileRef = reference.Should().BeOfType<FileReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.Path.Should().Be("folder/file.xlsx");
        fileRef.Partition.Should().Be("MS-2024");
    }

    [Fact]
    public void ContentPathHandler_Parse_HandlesNullPartition()
    {
        // arrange
        var handler = new ContentPathHandler();

        // act
        var (address, reference) = handler.Parse("host", "1", "Submissions/file.xlsx");

        // assert
        address.Type.Should().Be("host");
        address.Id.Should().Be("1");
        var fileRef = reference.Should().BeOfType<FileReference>().Subject;
        fileRef.Collection.Should().Be("Submissions");
        fileRef.Path.Should().Be("file.xlsx");
        fileRef.Partition.Should().BeNull();
    }

    [Fact]
    public void UnifiedPathRegistry_Register_And_TryResolve_Works()
    {
        // arrange
        var registry = new UnifiedPathRegistry();
        registry.Register("data", new DataPathHandler());
        registry.Register("area", new AreaPathHandler()); // Need area for default fallback

        // act - new format: addressType/addressId/keyword/remainingPath
        var found = registry.TryResolve("pricing/MS-2024/data/Collection", out var address, out var reference);

        // assert
        found.Should().BeTrue();
        address!.Type.Should().Be("pricing");
        address.Id.Should().Be("MS-2024");
        var dataPathRef = reference.Should().BeOfType<DataPathReference>().Subject;
        dataPathRef.Path.Should().Be("Collection");
    }

    [Fact]
    public void UnifiedPathRegistry_TryResolve_ReturnsFalse_ForInvalidPath()
    {
        // arrange
        var registry = new UnifiedPathRegistry();

        // act - single segment is invalid (need at least addressType/addressId)
        var found = registry.TryResolve("unknown", out var address, out var reference);

        // assert
        found.Should().BeFalse();
        address.Should().BeNull();
        reference.Should().BeNull();
    }

    [Fact]
    public void UnifiedPathRegistry_IsCaseInsensitive()
    {
        // arrange
        var registry = new UnifiedPathRegistry();
        registry.Register("DATA", new DataPathHandler());
        registry.Register("area", new AreaPathHandler()); // Need area for default fallback

        // act - keyword matching is case-insensitive
        var found = registry.TryResolve("pricing/MS-2024/data/Collection", out var address, out _);

        // assert
        found.Should().BeTrue();
        address.Should().NotBeNull();
    }

    [Fact]
    public void UnifiedPathRegistry_Keywords_ReturnsAllRegistered()
    {
        // arrange
        var registry = new UnifiedPathRegistry();
        registry.Register("data", new DataPathHandler());
        registry.Register("area", new AreaPathHandler());
        registry.Register("content", new ContentPathHandler());

        // act
        var keywords = registry.Keywords.ToList();

        // assert
        keywords.Should().Contain("data");
        keywords.Should().Contain("area");
        keywords.Should().Contain("content");
        keywords.Should().HaveCount(3);
    }

    #endregion

    #region Custom DataPath and Global Registry Tests

    [Fact]
    public async Task DataPathReference_LocalResolution_ReturnsCollection()
    {
        // arrange
        GetHost();
        var client = GetClient();

        // act - use DataPathReference for local path resolution
        var response = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("TestPricing")),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        response.Message.Data.Should().BeOfType<InstanceCollection>();
    }

    [Fact]
    public async Task DataPathReference_LocalResolution_ReturnsEntity()
    {
        // arrange
        GetHost();
        var client = GetClient();

        // act - use DataPathReference for local path resolution
        var response = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference($"TestPricing/{TestPricingId}")),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var pricing = response.Message.Data.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public void ContentWorkspaceReference_ToString_FormatsCorrectly()
    {
        // arrange
        var refWithoutPartition = new ContentWorkspaceReference("TestCollection", "path/to/file.txt");
        var refWithPartition = new ContentWorkspaceReference("TestCollection", "path/to/file.txt", "partition1");

        // act & assert
        refWithoutPartition.ToString().Should().Be("TestCollection/path/to/file.txt");
        refWithPartition.ToString().Should().Be("TestCollection@partition1/path/to/file.txt");
    }

    [Fact]
    public async Task ContentWorkspaceReference_GetData_ReturnsFileContent()
    {
        // arrange - create test file
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_ContentRef_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "ref-test.txt");
        await File.WriteAllTextAsync(testFilePath, "Content workspace reference test", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();

            // act - use ContentWorkspaceReference directly
            var response = await client.AwaitResponse(
                new GetDataRequest(new ContentWorkspaceReference("TestFiles", "ref-test.txt")),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            response.Message.Error.Should().BeNull();
            response.Message.Data.Should().NotBeNull();
            var content = response.Message.Data as string;
            content.Should().Contain("Content workspace reference test");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task FileReference_GetData_ReturnsFileContent()
    {
        // arrange - create test file
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_FileRef_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFilePath = Path.Combine(testDir, "fileref-test.txt");
        await File.WriteAllTextAsync(testFilePath, "File reference test content", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();

            // act - use FileReference directly
            var response = await client.AwaitResponse(
                new GetDataRequest(new FileReference("TestFiles", "fileref-test.txt")),
                o => o.WithTarget(new HostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            response.Message.Error.Should().BeNull();
            response.Message.Data.Should().NotBeNull();
            var content = response.Message.Data as string;
            content.Should().Contain("File reference test content");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public void UnifiedPathRegistry_CustomKeywordHandler_CanBeRegistered()
    {
        // arrange - create a custom keyword handler for a domain-specific path
        var registry = new UnifiedPathRegistry();
        var customHandler = new CustomPricingKeywordHandler();

        // act
        registry.Register("pricing", customHandler);
        registry.Register("area", new AreaPathHandler()); // Need area for default fallback

        // assert
        registry.Keywords.Should().Contain("pricing");
        // Format: addressType/addressId/keyword/remainingPath
        registry.TryResolve("company/MS-2024/pricing/details", out var address, out var reference).Should().BeTrue();
        address!.Type.Should().Be("company");
        address.Id.Should().Be("MS-2024");
        reference.Should().BeOfType<EntityReference>();
    }

    [Fact]
    public void UnifiedPathRegistry_MultipleCustomKeywords_CoexistWithBuiltIn()
    {
        // arrange
        var registry = new UnifiedPathRegistry();

        // Register built-in handlers
        registry.Register("data", new DataPathHandler());
        registry.Register("area", new AreaPathHandler());
        registry.Register("content", new ContentPathHandler());

        // Register custom handlers
        registry.Register("pricing", new CustomPricingKeywordHandler());
        registry.Register("claims", new CustomClaimsKeywordHandler());

        // act & assert
        registry.Keywords.Should().HaveCount(5);

        // Built-in handlers work with new format
        registry.TryResolve("host/1/data/Collection", out _, out _).Should().BeTrue();
        registry.TryResolve("host/1/Overview", out _, out _).Should().BeTrue(); // area is default

        // Custom handlers work
        registry.TryResolve("company/MS-2024/pricing/details", out var pricingAddr, out var pricingRef).Should().BeTrue();
        pricingAddr!.Type.Should().Be("company");

        registry.TryResolve("company/CLM-001/claims/info", out var claimsAddr, out var claimsRef).Should().BeTrue();
        claimsAddr!.Type.Should().Be("company");
    }



    /// <summary>
    /// Custom pricing keyword handler for testing custom keyword registration.
    /// Uses the new format: addressType/addressId/pricing/remainingPath
    /// </summary>
    private class CustomPricingKeywordHandler : IUnifiedPathHandler
    {
        public (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath)
        {
            var address = new Address(addressType, addressId);
            var reference = new EntityReference("Pricing", remainingPath ?? addressId);
            return (address, reference);
        }
    }

    /// <summary>
    /// Custom claims keyword handler for testing custom keyword registration.
    /// Uses the new format: addressType/addressId/claims/remainingPath
    /// </summary>
    private class CustomClaimsKeywordHandler : IUnifiedPathHandler
    {
        public (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath)
        {
            var address = new Address(addressType, addressId);
            var reference = new EntityReference("Claims", remainingPath ?? addressId);
            return (address, reference);
        }
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
