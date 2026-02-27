using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Generic integration tests for verifying that node types correctly load data
/// from source documents (JSON files, Markdown files, etc.).
///
/// These tests verify the data loading infrastructure works correctly for any
/// node type that has source documents configured, not just specific use cases.
/// </summary>
[Collection("SourceDocumentDataLoadingTests")]
public class SourceDocumentDataLoadingTest : MonolithMeshTestBase
{
    public SourceDocumentDataLoadingTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddFileSystemPersistence(TestPaths.SamplesGraphData))
            .AddGraph();

    /// <summary>
    /// Test case definition for source document data loading tests.
    /// </summary>
    public record DataLoadingTestCase(
        string NodeAddress,
        string TypeName,
        int? ExpectedMinCount = null,
        string? ExpectedIdPattern = null,
        string Description = "")
    {
        public override string ToString() =>
            string.IsNullOrEmpty(Description)
                ? $"{NodeAddress} -> {TypeName}"
                : Description;
    }

    /// <summary>
    /// Test cases for Cornerstone pricing nodes.
    /// Add new test cases here when creating new use cases with source documents.
    /// </summary>
    public static IEnumerable<object[]> CornerstonePricingTestCases =>
        new List<object[]>
        {
            // Microsoft 2026 - PropertyRisks from JSON
            new object[] { new DataLoadingTestCase(
                "ACME/Insurance/Microsoft/2026",
                "PropertyRisk",
                ExpectedMinCount: 1,
                Description: "Microsoft 2026 should load PropertyRisks from JSON") },

            // Microsoft 2026 - ReinsuranceAcceptances from Slip.md
            new object[] { new DataLoadingTestCase(
                "ACME/Insurance/Microsoft/2026",
                "ReinsuranceAcceptance",
                ExpectedMinCount: 1,
                Description: "Microsoft 2026 should load ReinsuranceAcceptances from Slip.md") },

            // Microsoft 2026 - ReinsuranceSections from Slip.md
            new object[] { new DataLoadingTestCase(
                "ACME/Insurance/Microsoft/2026",
                "ReinsuranceSection",
                ExpectedMinCount: 1,
                Description: "Microsoft 2026 should load ReinsuranceSections from Slip.md") },
        };

    /// <summary>
    /// Verifies that a node type correctly loads data from its configured source documents.
    /// This is a parameterized test that can be used for any node type.
    /// </summary>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(CornerstonePricingTestCases))]
    public async Task NodeType_LoadsDataFromSourceDocuments(DataLoadingTestCase testCase)
    {
        var client = GetClient();
        var addressParts = testCase.NodeAddress.Split('/');
        var address = new Address(addressParts);

        // Initialize the node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        // Get the workspace
        var hub = Mesh.GetHostedHub(address);
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

        // Get data for the specified type
        var data = await GetDynamicObservable(workspace, testCase.TypeName)
            .Where(d => d != null && d.Length > 0)
            .Timeout(30.Seconds())
            .FirstAsync();

        // Verify data was loaded
        data.Should().NotBeNull($"{testCase.TypeName} should be loaded for {testCase.NodeAddress}");
        data.Should().NotBeEmpty($"{testCase.TypeName} should have data loaded from source documents");

        // Verify minimum count if specified
        if (testCase.ExpectedMinCount.HasValue)
        {
            data.Should().HaveCountGreaterThanOrEqualTo(
                testCase.ExpectedMinCount.Value,
                $"{testCase.TypeName} should have at least {testCase.ExpectedMinCount} records");
        }

        // Verify ID pattern if specified
        if (!string.IsNullOrEmpty(testCase.ExpectedIdPattern))
        {
            var ids = data.Select(d => GetProperty<string>(d, "Id")).Where(id => id != null).ToList();
            ids.Should().Contain(id => id!.Contains(testCase.ExpectedIdPattern),
                $"{testCase.TypeName} should have records with IDs matching '{testCase.ExpectedIdPattern}'");
        }
    }

    /// <summary>
    /// Verifies that a node hub can be initialized and has the expected data types registered.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("ACME/Insurance/Microsoft/2026", new[] { "PropertyRisk", "ReinsuranceAcceptance", "ReinsuranceSection" })]
    public async Task NodeHub_HasExpectedDataTypesRegistered(string nodeAddress, string[] expectedTypes)
    {
        var client = GetClient();
        var addressParts = nodeAddress.Split('/');
        var address = new Address(addressParts);

        // Initialize the node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        // Get the type registry
        var hub = Mesh.GetHostedHub(address);
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        // Verify all expected types are registered
        foreach (var typeName in expectedTypes)
        {
            var type = typeRegistry.GetType(typeName);
            type.Should().NotBeNull($"Type '{typeName}' should be registered for {nodeAddress}");
        }
    }

    /// <summary>
    /// Verifies that source document files exist for a node type.
    /// This helps catch configuration issues early.
    /// </summary>
    [Theory(Timeout = 5000)]
    [InlineData("ACME/Insurance/Microsoft/2026", "Submissions/Slip.md")]
    public void SourceDocuments_ExistInFileSystem(string nodeAddress, string relativePath)
    {
        var basePath = TestPaths.SamplesGraphData;
        var addressParts = nodeAddress.Split('/');

        // Build the full path: basePath/Cornerstone/Microsoft/2026/Submissions/file
        var fullPath = Path.Combine(basePath, Path.Combine(addressParts), relativePath);

        File.Exists(fullPath).Should().BeTrue(
            $"Source document should exist at {fullPath}");
    }

    /// <summary>
    /// Verifies that data loaded from source documents has valid structure.
    /// Checks that key properties are present and have valid values.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("ACME/Insurance/Microsoft/2026", "PropertyRisk", "Id", "LocationName")]
    [InlineData("ACME/Insurance/Microsoft/2026", "ReinsuranceAcceptance", "Id", "Name")]
    [InlineData("ACME/Insurance/Microsoft/2026", "ReinsuranceSection", "Id", "Limit")]
    public async Task LoadedData_HasValidStructure(string nodeAddress, string typeName, params string[] requiredProperties)
    {
        var client = GetClient();
        var addressParts = nodeAddress.Split('/');
        var address = new Address(addressParts);

        // Initialize the node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        // Get the workspace and data
        var hub = Mesh.GetHostedHub(address);
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

        var data = await GetDynamicObservable(workspace, typeName)
            .Where(d => d != null && d.Length > 0)
            .Timeout(30.Seconds())
            .FirstAsync();

        // Verify each record has the required properties with non-null values
        foreach (var record in data)
        {
            var recordType = record.GetType();

            foreach (var propName in requiredProperties)
            {
                var prop = recordType.GetProperty(propName);
                prop.Should().NotBeNull($"{typeName} should have property '{propName}'");

                var value = prop!.GetValue(record);
                value.Should().NotBeNull($"{typeName}.{propName} should have a value");
            }
        }
    }

    #region Helper Methods

    /// <summary>
    /// Helper to get observable stream for a dynamically compiled type by name.
    /// </summary>
    private IObservable<object[]?> GetDynamicObservable(IWorkspace workspace, string typeName)
    {
        var typeRegistry = workspace.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var type = typeRegistry.GetType(typeName);
        if (type == null)
            throw new InvalidOperationException($"Type {typeName} not found in registry");

        // Use reflection to call GetObservable<T>(IWorkspace)
        var getObservableMethods = typeof(WorkspaceExtensions)
            .GetMethods()
            .Where(m => m.Name == nameof(WorkspaceExtensions.GetObservable) && m.IsGenericMethod);

        var getStreamMethod = getObservableMethods
            .First(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(IWorkspace))
            .MakeGenericMethod(type);

        var observable = getStreamMethod.Invoke(null, [workspace]);
        if (observable == null)
            return Observable.Return<object[]?>(null);

        // Convert IObservable<IReadOnlyCollection<T>> to IObservable<object[]?>
        var readOnlyCollectionType = typeof(IReadOnlyCollection<>).MakeGenericType(type);
        var selectMethod = typeof(Observable).GetMethods()
            .First(m => m.Name == "Select" && m.GetParameters().Length == 2 && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(readOnlyCollectionType, typeof(object[]));

        Func<object?, object[]?> selector = items =>
            items == null ? null : ((IEnumerable)items).Cast<object>().ToArray();

        return (IObservable<object[]?>)selectMethod.Invoke(null, [observable, selector])!;
    }

    /// <summary>
    /// Helper to get property value from a dynamic object.
    /// </summary>
    private static T? GetProperty<T>(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName);
        if (prop == null) return default;
        var value = prop.GetValue(obj);
        if (value == null) return default;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    #endregion
}

/// <summary>
/// Collection definition for SourceDocumentDataLoadingTests.
/// Ensures tests in this collection run serially to avoid resource contention.
/// </summary>
[CollectionDefinition("SourceDocumentDataLoadingTests", DisableParallelization = true)]
public class SourceDocumentDataLoadingTestsCollection
{
}
