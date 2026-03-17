using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the unified content access system using GetDataRequest with UnifiedReference.
/// Tests workspace references like DataPathReference, LayoutAreaReference, FileReference, etc.
/// </summary>
public class UnifiedContentAccessTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestPricingId = "test-company-2024";

    #region GetDataRequest with UnifiedReference Tests

    [Fact]
    public async Task GetDataRequest_UnifiedReference_Collection_ReturnsAllEntities()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "data:TestPricing";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = $"data:TestPricing/{TestPricingId}";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "data:";

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        var pricing = dataResponse.Data.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_AreaPath_ReturnsLayoutAreaEntityStore()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = "area:TestArea"; // area is default, no keyword needed

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        // LayoutAreaReference returns EntityStore which contains the area's state
        dataResponse.Data.Should().BeOfType<EntityStore>();
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_AreaPath_WithId_ReturnsLayoutAreaJson()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();
        var path = $"area:TestArea/{TestPricingId}"; // area is default, no keyword needed

        // act - send from client to host
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_EmptyPath_ReturnsDefaultData()
    {
        // arrange - client sends request to host
        GetHost(); // Ensure host is initialized
        var client = GetClient();

        // act - send from client to host (empty path defaults to "data:" which returns default data)
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert - empty path should return default data (same as "data:")
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        var pricing = dataResponse.Data.Should().BeOfType<TestPricing>().Subject;
        pricing.Id.Should().Be(TestPricingId);
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
            var path = "content:TestFiles/content-test.txt";

            // act - send from client to host
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(CreateHostAddress()),
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
            var path = "content:TestFiles/test.txt";

            // act - send from client to host
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(CreateHostAddress()),
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
            var path = "content:TestFiles/multiline.txt";

            // act - request only 2 rows
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path) { NumberOfRows = 2 }),
                o => o.WithTarget(CreateHostAddress()),
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
            var path = "content:TestFiles/nonexistent.txt";

            // act
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(CreateHostAddress()),
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
            var path = "content:TestFiles/subfolder/nested.txt";

            // act
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference(path)),
                o => o.WithTarget(CreateHostAddress()),
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
        hostWithFileProvider = Mesh.GetHostedHub(CreateHostAddress(), ConfigureHostWithFileProvider(testDir));
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

    [Fact]
    public async Task GetDataRequest_UnifiedReference_LayoutAreas_ReturnsAreaDefinitions()
    {
        // arrange
        GetHost();
        var client = GetClient();
        var path = "layoutAreas:";

        // act
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();

        // Should contain list of LayoutAreaDefinition with "TestArea"
        var areas = dataResponse.Data as IList<LayoutAreaDefinition>;
        areas.Should().NotBeNull();
        areas.Should().Contain(a => a.Area == "TestArea");
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
        var path = $"data:TestPricing/{TestPricingId}";

        // First, verify the entity exists
        var getResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);
        getResponse.Message.Error.Should().BeNull();

        // act - update the entity
        var updatedPricing = new TestPricing { Id = TestPricingId, Name = "Updated Pricing", Status = "Draft" };
        var updateResponse = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest(path, updatedPricing),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Error.Should().BeNull();
        updateResponse.Message.Version.Should().BeGreaterThan(0);

        // Verify the update took effect
        var verifyResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
            o => o.WithTarget(CreateHostAddress()),
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
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "data:";

        // act
        var response = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest(path, new { }),
            o => o.WithTarget(CreateHostAddress()),
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
            var path = "content:TestFiles/update-test.txt";

            // act
            var response = await client.AwaitResponse(
                new UpdateUnifiedReferenceRequest(path, "Updated content"),
                o => o.WithTarget(CreateHostAddress()),
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
        var path = "area:TestArea"; // area is default

        // act
        var response = await client.AwaitResponse(
            new UpdateUnifiedReferenceRequest(path, new { }),
            o => o.WithTarget(CreateHostAddress()),
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
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Verify it exists
        var path = $"data:TestPricing/{newEntityId}";
        var getResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);
        getResponse.Message.Data.Should().NotBeNull();

        // act - delete the entity
        var deleteResponse = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        deleteResponse.Message.Success.Should().BeTrue();
        deleteResponse.Message.Error.Should().BeNull();

        // Verify it was deleted
        var verifyResponse = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "data:";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "data:TestPricing";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(CreateHostAddress()),
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
            var path = "content:TestFiles/delete-test.txt";

            // Verify file exists
            File.Exists(testFilePath).Should().BeTrue();

            // act
            var response = await client.AwaitResponse(
                new DeleteUnifiedReferenceRequest(path),
                o => o.WithTarget(CreateHostAddress()),
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
        var path = "area:TestArea"; // area is default

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "data:TestPricing/nonexistent-entity-id";

        // act
        var response = await client.AwaitResponse(
            new DeleteUnifiedReferenceRequest(path),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "data:TestPricing";

        // act - use GetDataRequest which correctly handles the UnifiedReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = $"data:TestPricing/{TestPricingId}";

        // act - use GetDataRequest which correctly handles the UnifiedReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
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
        var path = "area:TestArea"; // area is default, no keyword needed

        // act - use GetDataRequest which correctly handles the UnifiedReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference(path)),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        // LayoutAreaReference returns EntityStore which contains the area's state
        response.Message.Data.Should().BeOfType<EntityStore>();
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
            o => o.WithTarget(CreateHostAddress()),
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
            o => o.WithTarget(CreateHostAddress()),
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
                o => o.WithTarget(CreateHostAddress()),
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
                o => o.WithTarget(CreateHostAddress()),
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

    #endregion

    #region Content Collection Listing Tests

    [Fact]
    public async Task GetDataRequest_UnifiedReference_ContentListRoot_ReturnsFilesAndFolders()
    {
        // arrange - create test files with subfolder
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_List_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        await File.WriteAllTextAsync(Path.Combine(testDir, "readme.md"), "# Hello", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(testDir, "data.json"), "{}", TestContext.Current.CancellationToken);
        var subDir = Path.Combine(testDir, "images");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "logo.svg"), "<svg></svg>", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();

            // act - content:TestFiles/ lists the named collection root (trailing slash = browse)
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("content:TestFiles/")),
                o => o.WithTarget(CreateHostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().BeNull();
            dataResponse.Data.Should().NotBeNull();

            var items = dataResponse.Data as IReadOnlyCollection<ContentCollections.CollectionItem>;
            items.Should().NotBeNull();
            items.Should().Contain(i => i.Name == "readme.md");
            items.Should().Contain(i => i.Name == "data.json");
            items.Should().Contain(i => i.Name == "images");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task GetDataRequest_UnifiedReference_ContentListSubfolder_ReturnsSubfolderFiles()
    {
        // arrange - create test files with subfolder
        var testDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTest_SubList_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var subDir = Path.Combine(testDir, "images");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "logo.svg"), "<svg></svg>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "banner.png"), "fake-png", TestContext.Current.CancellationToken);

        try
        {
            var host = GetHostWithFileProvider(testDir);
            var client = GetClient();

            // act - content:TestFiles/images/ lists the subfolder (trailing slash = browse)
            var response = await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("content:TestFiles/images/")),
                o => o.WithTarget(CreateHostAddress()),
                TestContext.Current.CancellationToken);

            // assert
            var dataResponse = response.Message;
            dataResponse.Error.Should().BeNull();
            dataResponse.Data.Should().NotBeNull();

            var items = dataResponse.Data as IReadOnlyCollection<ContentCollections.CollectionItem>;
            items.Should().NotBeNull();
            items.Should().Contain(i => i.Name == "logo.svg");
            items.Should().Contain(i => i.Name == "banner.png");
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
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
