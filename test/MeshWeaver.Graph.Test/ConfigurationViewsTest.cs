using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Comprehensive tests for ConfigurationViews including:
/// - View retrieval via layout streams
/// - DataChangeRequest persistence
/// - Full edit/save/verify flow
/// </summary>
public class ConfigurationViewsTest : HubTestBase
{
    private readonly string _testDataDirectory;

    public ConfigurationViewsTest(ITestOutputHelper output) : base(output)
    {
        // Create a temporary directory for test data
        _testDataDirectory = Path.Combine(Path.GetTempPath(), $"MeshWeaverTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDirectory);
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        // Use MeshHubBuilder which automatically registers DataModel and LayoutAreaConfig
        // with persistence when IConfigurationStorageService is available
        var testDataDirectory = _testDataDirectory;

        return new MeshHubBuilder(base.ConfigureHost(configuration)
                .WithServices(services =>
                {
                    // Register the storage service - MeshHubBuilder will use it for DataModel/LayoutAreaConfig persistence
                    services.AddSingleton<IConfigurationStorageService>(sp =>
                    {
                        var typeRegistry = sp.GetRequiredService<ITypeRegistry>();
                        return new ConfigurationStorageService(testDataDirectory, typeRegistry);
                    });
                    return services;
                })
                // Register types for serialization/deserialization with simple names
                .WithTypes(typeof(CodeEditorControl))
                .WithType<DataModel>(nameof(DataModel))
                .WithType<LayoutAreaConfig>(nameof(LayoutAreaConfig)))
            .WithMeshNavigation(false) // Don't need mesh navigation for these tests
            .WithHubConfiguration(config => config.AddLayout(layout => layout.AddConfigurationViews()))
            .Build();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            // Register types for serialization/deserialization with simple names to match host
            .WithTypes(typeof(CodeEditorControl))
            .WithType<DataModel>(nameof(DataModel))
            .WithType<LayoutAreaConfig>(nameof(LayoutAreaConfig))
            .AddLayoutClient(d => d)
            .AddData(data =>
                data.AddHubSource(CreateHostAddress(), dataSource => dataSource
                    .WithType<DataModel>()
                    .WithType<LayoutAreaConfig>())
            );

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Clean up test directory
        if (Directory.Exists(_testDataDirectory))
        {
            try
            {
                Directory.Delete(_testDataDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region DataModel Source View Tests

    [HubFact]
    public async Task DataModelSource_ReturnsMarkdownCodeBlock()
    {
        // Arrange - save a test DataModel
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var dataModel = new DataModel
        {
            Id = "test-model",
            DisplayName = "Test Model",
            TypeSource = @"public record TestModel { public string Name { get; init; } }"
        };
        await storage.SaveAsync(dataModel);

        // Act - request the DataModelSource view
        var reference = new LayoutAreaReference(ConfigurationViews.DataModelSource) { Id = "test-model" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert - should be a Stack with markdown code block
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCountGreaterThan(0);
    }

    [HubFact]
    public async Task DataModelSource_WithMissingId_ReturnsError()
    {
        // Act - request without an ID
        var reference = new LayoutAreaReference(ConfigurationViews.DataModelSource);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert - should return error markdown
        control.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("not specified");
    }

    #endregion

    #region DataModel Editor View Tests

    [HubFact]
    public async Task DataModelEditor_ReturnsMonacoEditor()
    {
        // Arrange - save a test DataModel
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var dataModel = new DataModel
        {
            Id = "edit-model",
            DisplayName = "Edit Model",
            TypeSource = @"public record EditModel { public int Value { get; init; } }"
        };
        await storage.SaveAsync(dataModel);

        // Act - request the DataModelEditor view
        var reference = new LayoutAreaReference(ConfigurationViews.DataModelEditor) { Id = "edit-model" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert - should be a Stack containing CodeEditorControl
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCountGreaterThan(0);
    }

    #endregion

    #region LayoutAreaConfig Source View Tests

    [HubFact]
    public async Task LayoutAreaSource_ReturnsMarkdownCodeBlock()
    {
        // Arrange - save a test LayoutAreaConfig
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var layoutArea = new LayoutAreaConfig
        {
            Id = "test-area",
            Area = "TestArea",
            Title = "Test Area",
            ViewSource = @"public static UiControl Render(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(""Test"");"
        };
        await storage.SaveAsync(layoutArea);

        // Act - request the LayoutAreaSource view
        var reference = new LayoutAreaReference(ConfigurationViews.LayoutAreaSource) { Id = "test-area" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert - should be a Stack with markdown code block
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCountGreaterThan(0);
    }

    [HubFact]
    public async Task LayoutAreaSource_WithNoViewSource_ShowsPlaceholder()
    {
        // Arrange - save a LayoutAreaConfig without ViewSource
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var layoutArea = new LayoutAreaConfig
        {
            Id = "no-source-area",
            Area = "NoSourceArea",
            Title = "No Source Area",
            ViewSource = null
        };
        await storage.SaveAsync(layoutArea);

        // Act - request the LayoutAreaSource view
        var reference = new LayoutAreaReference(ConfigurationViews.LayoutAreaSource) { Id = "no-source-area" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert - should still render (with placeholder comment)
        control.Should().BeOfType<StackControl>();
    }

    #endregion

    #region LayoutAreaConfig Editor View Tests

    [HubFact]
    public async Task LayoutAreaEditor_ReturnsMonacoEditor()
    {
        // Arrange - save a test LayoutAreaConfig
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var layoutArea = new LayoutAreaConfig
        {
            Id = "edit-area",
            Area = "EditArea",
            Title = "Edit Area",
            ViewSource = @"public static UiControl Render(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(""Edit"");"
        };
        await storage.SaveAsync(layoutArea);

        // Act - request the LayoutAreaEditor view
        var reference = new LayoutAreaReference(ConfigurationViews.LayoutAreaEditor) { Id = "edit-area" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert - should be a Stack containing editor components
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCountGreaterThan(0);
    }

    #endregion

    #region DataChangeRequest Persistence Tests

    [HubFact]
    public async Task DataChangeRequest_SavesDataModel()
    {
        // Arrange
        var host = GetHost();
        var client = GetClient();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        // Initialize by subscribing to the data stream via client
        var dataModels = await client.GetWorkspace().GetObservable<DataModel>()
            .Timeout(5.Seconds())
            .FirstAsync();

        var dataModel = new DataModel
        {
            Id = "save-test-model",
            DisplayName = "Save Test",
            TypeSource = @"public record SaveTestModel { public string Data { get; init; } }"
        };

        // Act - send DataChangeRequest to HOST (where storage TypeSource is)
        // NOTE: changedBy must be non-null for the Synchronize filter to allow the update
        var response = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { dataModel }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);

        response.Message.Should().BeOfType<DataChangeResponse>();

        // Wait for persistence to complete
        await Task.Delay(200);

        // Assert - verify the model was saved to storage
        var savedModel = await storage.LoadByIdAsync<DataModel>("save-test-model");
        savedModel.Should().NotBeNull();
        savedModel!.DisplayName.Should().Be("Save Test");
        savedModel.TypeSource.Should().Contain("SaveTestModel");
    }

    [HubFact]
    public async Task DataChangeRequest_SavesLayoutAreaConfig()
    {
        // Arrange
        var host = GetHost();
        var client = GetClient();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        // Initialize by subscribing to the data stream via client
        var configs = await client.GetWorkspace().GetObservable<LayoutAreaConfig>()
            .Timeout(5.Seconds())
            .FirstAsync();

        var layoutArea = new LayoutAreaConfig
        {
            Id = "save-test-area",
            Area = "SaveTestArea",
            Title = "Save Test Area",
            ViewSource = @"public static UiControl View(LayoutAreaHost h, RenderingContext c) => Controls.Html(""Saved"");"
        };

        // Act - send DataChangeRequest to HOST (where storage TypeSource is)
        var response = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { layoutArea }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);

        response.Message.Should().BeOfType<DataChangeResponse>();

        // Wait for persistence to complete
        await Task.Delay(200);

        // Assert - verify the config was saved to storage
        var savedConfig = await storage.LoadByIdAsync<LayoutAreaConfig>("save-test-area");
        savedConfig.Should().NotBeNull();
        savedConfig!.Title.Should().Be("Save Test Area");
        savedConfig.ViewSource.Should().Contain("Saved");
    }

    [HubFact]
    public async Task DataChangeRequest_UpdatesExistingDataModel()
    {
        // Arrange - first create a model via DataChangeRequest
        var host = GetHost();
        var client = GetClient();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        // Initialize by subscribing to the data stream via client
        var dataModels = await client.GetWorkspace().GetObservable<DataModel>()
            .Timeout(5.Seconds())
            .FirstAsync();

        var originalModel = new DataModel
        {
            Id = "update-test-model",
            DisplayName = "Original Name",
            TypeSource = @"public record UpdateTestModel { public string Original { get; init; } }"
        };

        // Create the original model via DataChangeRequest to HOST
        var createResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { originalModel }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);

        createResponse.Message.Should().BeOfType<DataChangeResponse>();
        await Task.Delay(200);

        // Act - update via DataChangeRequest to HOST
        var updatedModel = originalModel with
        {
            DisplayName = "Updated Name",
            TypeSource = @"public record UpdateTestModel { public string Updated { get; init; } }"
        };

        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updatedModel }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);

        updateResponse.Message.Should().BeOfType<DataChangeResponse>();

        // Wait for persistence to complete
        await Task.Delay(200);

        // Assert - verify the model was updated
        var savedModel = await storage.LoadByIdAsync<DataModel>("update-test-model");
        savedModel.Should().NotBeNull();
        savedModel!.DisplayName.Should().Be("Updated Name");
        savedModel.TypeSource.Should().Contain("Updated");
    }

    #endregion

    #region Full Edit Flow Tests

    [HubFact]
    public async Task FullFlow_LoadEditSaveVerify_DataModel()
    {
        // Arrange - create initial model via DataChangeRequest
        var host = GetHost();
        var client = GetClient();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();
        var workspace = client.GetWorkspace();

        // Initialize by subscribing to the data stream
        await workspace.GetObservable<DataModel>()
            .Timeout(5.Seconds())
            .FirstAsync();

        var initialModel = new DataModel
        {
            Id = "full-flow-model",
            DisplayName = "Initial",
            TypeSource = @"public record FullFlowModel { public string Initial { get; init; } }"
        };

        // Create initial model via DataChangeRequest
        var createResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { initialModel }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);
        createResponse.Message.Should().BeOfType<DataChangeResponse>();
        await Task.Delay(200);

        // Step 1: Load and verify via source view
        var sourceRef = new LayoutAreaReference(ConfigurationViews.DataModelSource) { Id = "full-flow-model" };
        var sourceStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            sourceRef
        );

        var sourceControl = await sourceStream.GetControlStream(sourceRef.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        sourceControl.Should().BeOfType<StackControl>();

        // Step 2: Load editor view
        var editorRef = new LayoutAreaReference(ConfigurationViews.DataModelEditor) { Id = "full-flow-model" };
        var editorStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            editorRef
        );

        var editorControl = await editorStream.GetControlStream(editorRef.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        editorControl.Should().BeOfType<StackControl>();

        // Step 3: Update model via DataChangeRequest
        var updatedModel = initialModel with
        {
            DisplayName = "Modified",
            TypeSource = @"public record FullFlowModel { public string Modified { get; init; } }"
        };

        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updatedModel }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();

        // Wait for persistence
        await Task.Delay(200);

        // Step 4: Verify persisted change
        var savedModel = await storage.LoadByIdAsync<DataModel>("full-flow-model");
        savedModel.Should().NotBeNull();
        savedModel!.DisplayName.Should().Be("Modified");
        savedModel.TypeSource.Should().Contain("Modified");
    }

    [HubFact]
    public async Task FullFlow_LoadEditSaveVerify_LayoutAreaConfig()
    {
        // Arrange - create initial config via DataChangeRequest
        var host = GetHost();
        var client = GetClient();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();
        var workspace = client.GetWorkspace();

        // Initialize by subscribing to the data stream
        await workspace.GetObservable<LayoutAreaConfig>()
            .Timeout(5.Seconds())
            .FirstAsync();

        var initialConfig = new LayoutAreaConfig
        {
            Id = "full-flow-area",
            Area = "FullFlowArea",
            Title = "Initial Title",
            ViewSource = @"public static UiControl Initial(LayoutAreaHost h, RenderingContext c) => Controls.Html(""Initial"");"
        };

        // Create initial config via DataChangeRequest
        var createResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { initialConfig }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);
        createResponse.Message.Should().BeOfType<DataChangeResponse>();
        await Task.Delay(200);

        // Step 1: Load and verify via source view
        var sourceRef = new LayoutAreaReference(ConfigurationViews.LayoutAreaSource) { Id = "full-flow-area" };
        var sourceStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            sourceRef
        );

        var sourceControl = await sourceStream.GetControlStream(sourceRef.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        sourceControl.Should().BeOfType<StackControl>();

        // Step 2: Load editor view
        var editorRef = new LayoutAreaReference(ConfigurationViews.LayoutAreaEditor) { Id = "full-flow-area" };
        var editorStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            editorRef
        );

        var editorControl = await editorStream.GetControlStream(editorRef.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        editorControl.Should().BeOfType<StackControl>();

        // Step 3: Update config via DataChangeRequest
        var updatedConfig = initialConfig with
        {
            Title = "Modified Title",
            ViewSource = @"public static UiControl Modified(LayoutAreaHost h, RenderingContext c) => Controls.Html(""Modified"");"
        };

        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updatedConfig }, changedBy: "test"),
            o => o.WithTarget(CreateHostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token);
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();

        // Wait for persistence
        await Task.Delay(200);

        // Step 4: Verify persisted change
        var savedConfig = await storage.LoadByIdAsync<LayoutAreaConfig>("full-flow-area");
        savedConfig.Should().NotBeNull();
        savedConfig!.Title.Should().Be("Modified Title");
        savedConfig.ViewSource.Should().Contain("Modified");
    }

    #endregion

    #region Storage Service Integration Tests

    [HubFact]
    public async Task ConfigurationStorageService_LoadByIdAsync_ReturnsDataModel()
    {
        // Arrange
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var dataModel = new DataModel
        {
            Id = "load-by-id-model",
            DisplayName = "Load Test",
            TypeSource = @"public record LoadModel { }"
        };
        await storage.SaveAsync(dataModel);

        // Act
        var loaded = await storage.LoadByIdAsync<DataModel>("load-by-id-model");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("load-by-id-model");
        loaded.DisplayName.Should().Be("Load Test");
    }

    [HubFact]
    public async Task ConfigurationStorageService_LoadByIdAsync_ReturnsLayoutAreaConfig()
    {
        // Arrange
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        var config = new LayoutAreaConfig
        {
            Id = "load-by-id-area",
            Area = "LoadArea",
            Title = "Load Test"
        };
        await storage.SaveAsync(config);

        // Act
        var loaded = await storage.LoadByIdAsync<LayoutAreaConfig>("load-by-id-area");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("load-by-id-area");
        loaded.Title.Should().Be("Load Test");
    }

    [HubFact]
    public async Task ConfigurationStorageService_LoadByIdAsync_ReturnsNullForMissing()
    {
        // Arrange
        var host = GetHost();
        var storage = host.ServiceProvider.GetRequiredService<IConfigurationStorageService>();

        // Act
        var loaded = await storage.LoadByIdAsync<DataModel>("non-existent-id");

        // Assert
        loaded.Should().BeNull();
    }

    #endregion
}
